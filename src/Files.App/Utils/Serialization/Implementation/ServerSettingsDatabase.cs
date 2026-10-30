// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Server;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using Windows.Storage;

namespace Files.App.Utils.Serialization.Implementation;

internal sealed class ServerSettingsDatabase : IJsonSettingsDatabase
{
	private readonly string fileName;
	private readonly IJsonSettingsSerializer serializer;
	private readonly JsonSerializerContext context;
	private readonly Dictionary<string, JsonElement> defaults = [];
	private readonly Lock gate = new();
	private Dictionary<string, JsonElement> cache;
	private int cacheVersion;
	private readonly string localPath;
	// WinRT SettingsService.SetSetting AVs (0xc0000005) on this Win10 VM during SettingsValue[] marshalling.
	// Reads via GetSettings work; writes go through a local JSON file the server watcher can pick up.
	private static readonly bool PreferLocalWrites =
		string.Equals(Environment.GetEnvironmentVariable("FILES_LOCAL_SETTINGS_WRITES"), "0", StringComparison.Ordinal) is false;

	internal event Action<string, JsonElement>? Changed;

	public ServerSettingsDatabase(string fileName, IJsonSettingsSerializer serializer, JsonSerializerContext context)
	{
		this.fileName = fileName;
		this.serializer = serializer;
		this.context = context;
		var directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "settings");
		Directory.CreateDirectory(directory);
		localPath = Path.Combine(directory, fileName);
		cache = ReadValues();
		App.ServerChanged += OnServerChanged;
	}

	private Dictionary<string, JsonElement> ReadValues()
	{
		try
		{
			return SettingsValueCodec.DecodeObject(Server.SettingsService.GetSettings(fileName));
		}
		catch (Exception)
		{
			return ReadLocalFile();
		}
	}

	private Dictionary<string, JsonElement> ReadLocalFile()
	{
		var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		if (!File.Exists(localPath))
			return result;
		try
		{
			using var stream = File.OpenRead(localPath);
			if (stream.Length == 0)
				return result;
			using var document = JsonDocument.Parse(stream);
			if (document.RootElement.ValueKind == JsonValueKind.Object)
				foreach (var property in document.RootElement.EnumerateObject())
					result[property.Name] = property.Value.Clone();
		}
		catch (JsonException)
		{
		}
		return result;
	}

	private void PersistLocalUnlocked()
	{
		var temporary = localPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();
				foreach (var pair in cache)
				{
					writer.WritePropertyName(pair.Key);
					pair.Value.WriteTo(writer);
				}
				writer.WriteEndObject();
				writer.Flush();
				stream.Flush(true);
			}
			File.Move(temporary, localPath, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	public TValue? GetValue<TValue>(string key, TValue? defaultValue = default)
	{
		lock (gate)
		{
			if (cache.TryGetValue(key, out var value))
				return GetValueFromElement<TValue>(value) ?? defaultValue;
			if (defaults.TryGetValue(key, out var fallback))
				return GetValueFromElement<TValue>(fallback) ?? defaultValue;
			defaults[key] = GetElementFromValue(defaultValue);
			return defaultValue;
		}
	}

	public bool SetValue<TValue>(string key, TValue? value)
	{
		var element = GetElementFromValue(value);
		lock (gate)
		{
			var localChanged = !cache.TryGetValue(key, out var old) || !JsonElement.DeepEquals(old, element);
			cache[key] = element;
			cacheVersion++;
			defaults.Remove(key);
			if (!localChanged)
				return false;

			if (PreferLocalWrites)
			{
				PersistLocalUnlocked();
				return true;
			}

			var changed = Server.SettingsService.SetSetting(fileName, key, SettingsValueCodec.Encode(element));
			return changed || localChanged;
		}
	}

	public bool RemoveKey(string key)
	{
		lock (gate)
		{
			var removed = cache.Remove(key);
			cacheVersion++;
			if (PreferLocalWrites)
			{
				if (removed)
					PersistLocalUnlocked();
				return removed;
			}
			var changed = Server.SettingsService.RemoveSetting(fileName, key);
			return changed;
		}
	}

	public bool ImportSettings(object? import)
	{
		var values = import switch
		{
			IDictionary<string, JsonElement> elements => elements,
			IDictionary<string, object?> objects => objects.ToDictionary(pair => pair.Key, pair => GetElementFromValue(pair.Value)),
			_ => null,
		};
		if (values is null)
			return false;
		lock (gate)
		{
			cache = new Dictionary<string, JsonElement>(values, StringComparer.Ordinal);
			cacheVersion++;
			if (PreferLocalWrites)
			{
				PersistLocalUnlocked();
				return true;
			}
			var changed = Server.SettingsService.ImportSettings(fileName, SettingsValueCodec.EncodeObject(values));
			var flushed = FlushSettings();
			return changed && flushed;
		}
	}

	public IDictionary<string, JsonElement> ExportSettings() => ReadValues();

	public bool FlushSettings()
	{
		if (PreferLocalWrites)
		{
			lock (gate)
				PersistLocalUnlocked();
			return true;
		}
		return Server.SettingsService.FlushSettings(fileName);
	}

	private JsonTypeInfo<T?> GetTypeInfo<T>() => context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T?>
		?? throw new InvalidOperationException($"JSON serialization metadata is missing for {typeof(T)}.");

	private JsonElement GetElementFromValue<T>(T? value) => serializer.SerializeToElement(value, GetTypeInfo<T>());

	private T? GetValueFromElement<T>(JsonElement element)
	{
		try
		{
			return serializer.DeserializeFromElement(element, GetTypeInfo<T>());
		}
		catch (JsonException)
		{
			return default;
		}
	}

	private async void OnServerChanged(object? sender, string name)
	{
		if (name != ServerChangeNames.Settings(fileName))
			return;
		await SafetyExtensions.IgnoreExceptions(async () =>
		{
			var differences = await Task.Run(() =>
			{
				while (true)
				{
					int version;
					lock (gate)
						version = cacheVersion;
					var fresh = ReadValues();
					lock (gate)
					{
						if (version != cacheVersion)
							continue;
						var previous = cache;
						cache = fresh;
						cacheVersion++;
						var changes = fresh.Where(pair => !previous.TryGetValue(pair.Key, out var old) || !JsonElement.DeepEquals(old, pair.Value)).ToList();
						changes.AddRange(previous.Keys.Except(fresh.Keys).Select(key => new KeyValuePair<string, JsonElement>(key, default)));
						return changes.ToArray();
					}
				}
			});
			foreach (var pair in differences)
				Changed?.Invoke(pair.Key, pair.Value);
		}, App.Logger);
	}
}
