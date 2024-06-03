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
using System.Collections.Concurrent;
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
	public override string ModuleVersion => "1.0.0";
	public required PluginConfig Config { get; set; } = new PluginConfig();
	public static PluginCapability<IK4WorldTextSharedAPI> Capability_SharedAPI { get; } = new("k4-worldtext:sharedapi");

	private ConcurrentDictionary<int, List<TextLine>> _currentTopLists = new();

	public void OnConfigParsed(PluginConfig config)
	{
		if (config.Version < Config.Version)
			base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

		this.Config = config;
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		LoadWorldTextFromFile();

		if (Config.TimeBasedUpdate)
		{
			AddTimer(Config.UpdateInterval, RefreshTopLists, TimerFlags.REPEAT);
		}

		RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
		{
			RefreshTopLists();
			return HookResult.Continue;
		});
	}

	public override void Unload(bool hotReload)
	{
		foreach (var messageID in _currentTopLists.Keys)
		{
			var checkAPI = Capability_SharedAPI.Get();
			if (checkAPI != null)
			{
				checkAPI.RemoveWorldText(messageID);
			}
		}
		_currentTopLists.Clear();
	}

	[ConsoleCommand("css_k4toplist", "Sets up the wall toplist of K4-System")]
	[RequiresPermissions("@css/root")]
	public void OnToplistAdd(CCSPlayerController player, CommandInfo command)
	{
		IK4WorldTextSharedAPI? checkAPI = Capability_SharedAPI.Get();
		if (checkAPI is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
			return;
		}

		Task.Run(async () =>
		{
			List<PlayerPlace> topList = await GetTopPlayersAsync(Config.TopCount);

			List<TextLine> linesList = new List<TextLine>
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

				Logger.LogInformation("Player {0} is on place {1} with {2} points", topplayer.Name, i + 1, topplayer.Points);
			}

			Server.NextWorldUpdate(() =>
			{
				int messageID = checkAPI.AddWorldTextAtPlayer(player, TextPlacement.Wall, linesList);

				var lineList = checkAPI.GetWorldTextLineEntities(messageID);
				if (lineList == null || lineList.Count == 0)
				{
					Logger.LogError("Failed to get world text line entities for message ID: {0}", messageID);
					return;
				}

				var location = lineList[0]?.AbsOrigin;
				var rotation = lineList[0]?.AbsRotation;

				if (location == null || rotation == null)
				{
					Logger.LogError("Failed to get location or rotation for message ID: {0}", messageID);
					return;
				}

				SaveWorldTextToFile(location, rotation);

				_currentTopLists[messageID] = linesList;
			});
		});
	}

	[ConsoleCommand("css_k4toprem", "Removes the closest wall toplist of K4-System")]
	[RequiresPermissions("@css/root")]
	public void OnToplistRemove(CCSPlayerController player, CommandInfo command)
	{
		IK4WorldTextSharedAPI? checkAPI = Capability_SharedAPI.Get();
		if (checkAPI is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
			return;
		}

		var target = _currentTopLists.Keys.ToList()
			.SelectMany(id =>
			{
				var entities = checkAPI.GetWorldTextLineEntities(id);
				return entities != null
					? entities.Select(entity => new { Id = id, Entity = entity })
					: Enumerable.Empty<dynamic>();
			})
			.Where(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null && DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) < 100)
			.OrderBy(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null ? DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) : float.MaxValue)
			.FirstOrDefault();

		if (target is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.Red}Move closer to the Toplist that you want to remove.");
			return;
		}

		checkAPI.RemoveWorldText(target.Id);
		_currentTopLists.TryRemove(target.Id, out List<TextLine> _);

		var mapName = Server.MapName;
		var path = Path.Combine(ModuleDirectory, $"{mapName}_toplists.json");
		if (File.Exists(path))
		{
			var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
			if (data == null) return;

			var worldTextData = data.FirstOrDefault(x => x.Location == target.Entity.AbsOrigin.ToString() && x.Rotation == target.Entity.AbsRotation.ToString());
			if (worldTextData != null)
			{
				data.Remove(worldTextData);
				File.WriteAllText(path, JsonSerializer.Serialize(data));
			}
		}

		command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}K4-TopList {ChatColors.Silver}] {ChatColors.Green}Toplist removed!");
	}

	private float DistanceTo(Vector a, Vector b)
	{
		return (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
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
			var existingData = File.ReadAllText(path);
			data = JsonSerializer.Deserialize<List<WorldTextData>>(existingData) ?? new List<WorldTextData>();
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

			foreach (var worldTextData in data)
			{
				var checkAPI = Capability_SharedAPI.Get();
				if (checkAPI != null && worldTextData.Location != null && worldTextData.Rotation != null)
				{
					var messageID = checkAPI.AddWorldText(TextPlacement.Wall, new List<TextLine>(), ParseVector(worldTextData.Location), ParseQAngle(worldTextData.Rotation));
					_currentTopLists[messageID] = new List<TextLine>();
				}
			}
		}
	}

	public static Vector ParseVector(string vectorString)
	{
		string[] components = vectorString.Split(' ');
		if (components.Length != 3)
			throw new ArgumentException("Invalid vector string format.");

		float x = float.Parse(components[0]);
		float y = float.Parse(components[1]);
		float z = float.Parse(components[2]);

		return new Vector(x, y, z);
	}

	public static QAngle ParseQAngle(string qangleString)
	{
		string[] components = qangleString.Split(' ');
		if (components.Length != 3)
			throw new ArgumentException("Invalid QAngle string format.");

		float x = float.Parse(components[0]);
		float y = float.Parse(components[1]);
		float z = float.Parse(components[2]);

		return new QAngle(x, y, z);
	}

	private void RefreshTopLists()
	{
		Task.Run(async () =>
		{
			List<PlayerPlace> topList = await GetTopPlayersAsync(Config.TopCount);

			List<TextLine> linesList = new List<TextLine>
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

			Server.NextWorldUpdate(() =>
			{
				AddTimer(1, () =>
				{
					var checkAPI = Capability_SharedAPI.Get();
					if (checkAPI != null)
					{
						foreach (int key in _currentTopLists.Keys)
						{
							_currentTopLists[key] = linesList;
							checkAPI.UpdateWorldText(key, linesList);
						}
					}
				});
			});
		});
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

				var result = await connection.QueryAsync<PlayerPlace>(query, new { TopCount = topCount });
				return result.ToList();
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
