using System.Drawing;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using K4WorldTextSharedAPI;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Timers;

namespace K4Toplist;

public class PluginConfig : BasePluginConfig
{
	[JsonPropertyName("TopCount")]
	public int TopCount { get; set; } = 5;
	[JsonPropertyName("TimeBasedUpdate")]
	public bool TimeBasedUpdate { get; set; } = false;
	[JsonPropertyName("UpdateInterval")]
	public int UpdateInterval { get; set; } = 60;
	[JsonPropertyName("DatabaseSettings")]
	public DatabaseSettings DatabaseSettings { get; set; } = new DatabaseSettings();
	[JsonPropertyName("TitleText")]
	public string TitleText { get; set; } = "----- Toplist -----";
	[JsonPropertyName("ConfigVersion")]
	public override int Version { get; set; } = 3;
}

public sealed class DatabaseSettings
{
	[JsonPropertyName("host")]
	public string Host { get; set; } = "localhost";
	[JsonPropertyName("username")]
	public string Username { get; set; } = "root";
	[JsonPropertyName("database")]
	public string Database { get; set; } = "database";
	[JsonPropertyName("password")]
	public string Password { get; set; } = "password";
	[JsonPropertyName("port")]
	public int Port { get; set; } = 3306;
	[JsonPropertyName("sslmode")]
	public string Sslmode { get; set; } = "none";
	[JsonPropertyName("table-prefix")]
	public string TablePrefix { get; set; } = "";
}

[MinimumApiVersion(205)]
public class PluginK4Toplist : BasePlugin, IPluginConfig<PluginConfig>
{
	public override string ModuleName => "K4-System @ Wall Toplist";
	public override string ModuleAuthor => "K4ryuu";
	public override string ModuleVersion => "1.0.2";
	public required PluginConfig Config { get; set; } = new PluginConfig();
	public static PluginCapability<IK4WorldTextSharedAPI> Capability_SharedAPI { get; } = new("k4-worldtext:sharedapi");

	private List<int> _currentTopLists = new();
	private CounterStrikeSharp.API.Modules.Timers.Timer? _updateTimer;

	public void OnConfigParsed(PluginConfig config)
	{
		if (config.Version < Config.Version)
			base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

		this.Config = config;
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		AddTimer(3, LoadWorldTextFromFile);

		if (Config.TimeBasedUpdate)
		{
			_updateTimer = AddTimer(Config.UpdateInterval, RefreshTopLists, TimerFlags.REPEAT);
		}

		RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
		{
			RefreshTopLists();
			return HookResult.Continue;
		});

		RegisterListener<Listeners.OnMapStart>((mapName) =>
		{
			AddTimer(1, LoadWorldTextFromFile);
		});

		RegisterListener<Listeners.OnMapEnd>(() =>
		{
			_currentTopLists.Clear();
		});
	}

	public override void Unload(bool hotReload)
	{
		_currentTopLists.ForEach(id => Capability_SharedAPI.Get()?.RemoveWorldText(id));
		_currentTopLists.Clear();
		_updateTimer?.Kill();
	}

	[ConsoleCommand("css_k4toplist", "Sets up the wall toplist of K4-System")]
	[RequiresPermissions("@css/root")]
	public void OnToplistAdd(CCSPlayerController player, CommandInfo command)
	{
		var checkAPI = Capability_SharedAPI.Get();
		if (checkAPI is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
			return;
		}

		Task.Run(async () =>
		{
			var topList = await GetTopPlayersAsync(Config.TopCount);
			var linesList = GetTopListTextLines(topList);

			Server.NextWorldUpdate(() =>
			{
				int messageID = checkAPI.AddWorldTextAtPlayer(player, TextPlacement.Wall, linesList);
				_currentTopLists.Add(messageID);

				var lineList = checkAPI.GetWorldTextLineEntities(messageID);
				if (lineList?.Count > 0)
				{
					var location = lineList[0]?.AbsOrigin;
					var rotation = lineList[0]?.AbsRotation;

					if (location != null && rotation != null)
					{
						SaveWorldTextToFile(location, rotation);
					}
					else
					{
						Logger.LogError("Failed to get location or rotation for message ID: {0}", messageID);
					}
				}
				else
				{
					Logger.LogError("Failed to get world text line entities for message ID: {0}", messageID);
				}
			});
		});
	}

	[ConsoleCommand("css_k4toprem", "Removes the closest wall toplist of K4-System")]
	[RequiresPermissions("@css/root")]
	public void OnToplistRemove(CCSPlayerController player, CommandInfo command)
	{
		var checkAPI = Capability_SharedAPI.Get();
		if (checkAPI is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
			return;
		}

		var target = _currentTopLists
			.SelectMany(id => checkAPI.GetWorldTextLineEntities(id)?.Select(entity => new { Id = id, Entity = entity }) ?? Enumerable.Empty<dynamic>())
			.Where(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null && DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) < 100)
			.OrderBy(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null ? DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) : float.MaxValue)
			.FirstOrDefault();

		if (target is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.Red}Move closer to the Toplist that you want to remove.");
			return;
		}

		checkAPI.RemoveWorldText(target.Id);
		_currentTopLists.Remove(target.Id);

		var mapName = Server.MapName;
		var path = Path.Combine(ModuleDirectory, $"{mapName}_toplists.json");

		if (File.Exists(path))
		{
			var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
			if (data != null)
			{
				Vector entityVector = target.Entity.AbsOrigin;

				data.RemoveAll(x =>
				{
					Vector location = ParseVector(x.Location);
					return location.X == entityVector.X &&
						   location.Y == entityVector.Y &&
						   x.Rotation == target.Entity.AbsRotation.ToString();
				});

				File.WriteAllText(path, JsonSerializer.Serialize(data));
			}
		}

		command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.Green}Toplist removed!");
	}

	private float DistanceTo(Vector a, Vector b)
	{
		float dx = a.X - b.X;
		float dy = a.Y - b.Y;
		float dz = a.Z - b.Z;
		return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	private void SaveWorldTextToFile(Vector location, QAngle rotation)
	{
		var mapName = Server.MapName;
		var path = Path.Combine(ModuleDirectory, $"{mapName}_toplists.json");
		var worldTextData = new WorldTextData
		{
			Location = location.ToString(),
			Rotation = rotation.ToString()
		};

		List<WorldTextData> data;
		if (File.Exists(path))
		{
			data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path)) ?? new List<WorldTextData>();
		}
		else
		{
			data = new List<WorldTextData>();
		}

		data.Add(worldTextData);

		File.WriteAllText(path, JsonSerializer.Serialize(data));
	}

	private void LoadWorldTextFromFile()
	{
		var mapName = Server.MapName;
		var path = Path.Combine(ModuleDirectory, $"{mapName}_toplists.json");

		if (File.Exists(path))
		{
			var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
			if (data == null) return;

			Task.Run(async () =>
			{
				var topList = await GetTopPlayersAsync(Config.TopCount);
				var linesList = GetTopListTextLines(topList);

				Server.NextWorldUpdate(() =>
				{
					var checkAPI = Capability_SharedAPI.Get();
					if (checkAPI is null) return;

					foreach (var worldTextData in data)
					{
						if (!string.IsNullOrEmpty(worldTextData.Location) && !string.IsNullOrEmpty(worldTextData.Rotation))
						{
							var messageID = checkAPI.AddWorldText(TextPlacement.Wall, linesList, ParseVector(worldTextData.Location), ParseQAngle(worldTextData.Rotation));
							_currentTopLists.Add(messageID);
						}
					}
				});
			});
		}
	}

	public static Vector ParseVector(string vectorString)
	{
		string[] components = vectorString.Split(' ');
		if (components.Length == 3 &&
			float.TryParse(components[0], out float x) &&
			float.TryParse(components[1], out float y) &&
			float.TryParse(components[2], out float z))
		{
			return new Vector(x, y, z);
		}
		throw new ArgumentException("Invalid vector string format.");
	}

	public static QAngle ParseQAngle(string qangleString)
	{
		string[] components = qangleString.Split(' ');
		if (components.Length == 3 &&
			float.TryParse(components[0], out float x) &&
			float.TryParse(components[1], out float y) &&
			float.TryParse(components[2], out float z))
		{
			return new QAngle(x, y, z);
		}
		throw new ArgumentException("Invalid QAngle string format.");
	}

	private void RefreshTopLists()
	{
		Task.Run(async () =>
		{
			var topList = await GetTopPlayersAsync(Config.TopCount);
			var linesList = GetTopListTextLines(topList);

			Server.NextWorldUpdate(() =>
			{
				AddTimer(1, () =>
				{
					var checkAPI = Capability_SharedAPI.Get();
					if (checkAPI != null)
					{
						foreach (int messageID in _currentTopLists)
						{
							checkAPI.UpdateWorldText(messageID, linesList);
						}
					}
				});
			});
		});
	}

	private List<TextLine> GetTopListTextLines(List<PlayerPlace> topList)
	{
		var linesList = new List<TextLine>
		{
			new TextLine
			{
				Text = Config.TitleText,
				Color = Color.Pink,
				FontSize = 24,
				FullBright = true,
				Scale = 0.45f
			}
		};

		for (int i = 0; i < topList.Count; i++)
		{
			var topplayer = topList[i];
			var color = i switch
			{
				0 => Color.Red,
				1 => Color.Orange,
				2 => Color.Yellow,
				_ => Color.White
			};

			linesList.Add(new TextLine
			{
				Text = $"{i + 1}. {topplayer.Name} - {topplayer.Points} points",
				Color = color,
				FontSize = 24,
				FullBright = true,
				Scale = 0.35f,
			});
		}

		return linesList;
	}

	public async Task<List<PlayerPlace>> GetTopPlayersAsync(int topCount)
	{
		try
		{
			var dbSettings = Config.DatabaseSettings;

			using (var connection = new MySqlConnection($@"Server={dbSettings.Host};Port={dbSettings.Port};Database={dbSettings.Database};
				Uid={dbSettings.Username};Pwd={dbSettings.Password};
				SslMode={Enum.Parse<MySqlSslMode>(dbSettings.Sslmode, true)};"))
			{
				string query = $@"
                WITH RankedPlayers AS (
                    SELECT
                        steam_id,
                        name,
                        points,
                        DENSE_RANK() OVER (ORDER BY points DESC) AS playerPlace
                    FROM `{dbSettings.TablePrefix}k4ranks`
                )
                SELECT steam_id, name, points, playerPlace
                FROM RankedPlayers
                ORDER BY points DESC
                LIMIT @TopCount";

				return (await connection.QueryAsync<PlayerPlace>(query, new { TopCount = topCount })).ToList();
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, "Failed to retrieve top players");
			return new List<PlayerPlace>();
		}
	}
}

public class PlayerPlace
{
	public required string Name { get; set; }
	public int Points { get; set; }
	public int Placement { get; set; }
}

public class WorldTextData
{
	public required string Location { get; set; }
	public required string Rotation { get; set; }
}
