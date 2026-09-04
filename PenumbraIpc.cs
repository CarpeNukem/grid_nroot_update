using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace GridNrootUpdate;

internal sealed class PenumbraIpc
{
    private readonly IDalamudPluginInterface pluginInterface;

    public PenumbraIpc(IDalamudPluginInterface pluginInterface)
        => this.pluginInterface = pluginInterface;

    public bool IsAvailable()
    {
        try
        {
            _ = pluginInterface.GetIpcSubscriber<(int Breaking, int Feature)>("Penumbra.ApiVersion.V5").InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            PluginService.Log.Debug(ex, "Penumbra IPC is not available.");
            return false;
        }
    }

    public int InstallMod(string packagePath)
        => pluginInterface.GetIpcSubscriber<string, int>("Penumbra.InstallMod.V5").InvokeFunc(packagePath);

    public int DeleteMod(string modDirectory, string modName)
        => pluginInterface.GetIpcSubscriber<string, string, int>("Penumbra.DeleteMod.V5").InvokeFunc(modDirectory, modName);

    public int SetModPath(string modDirectory, string modName, string newPath)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath.V5").InvokeFunc(modDirectory, newPath, modName);
        }
        catch
        {
            return pluginInterface.GetIpcSubscriber<string, string, string, int>("Penumbra.SetModPath").InvokeFunc(modDirectory, newPath, modName);
        }
    }

    public void SubscribeModAdded(Action<string> handler)
        => pluginInterface.GetIpcSubscriber<string, object>("Penumbra.ModAdded").Subscribe(handler);

    public void UnsubscribeModAdded(Action<string> handler)
        => pluginInterface.GetIpcSubscriber<string, object>("Penumbra.ModAdded").Unsubscribe(handler);

    public void SubscribeInitialized(Action handler)
        => pluginInterface.GetIpcSubscriber<object>("Penumbra.Initialized").Subscribe(handler);

    public void UnsubscribeInitialized(Action handler)
        => pluginInterface.GetIpcSubscriber<object>("Penumbra.Initialized").Unsubscribe(handler);

    public void SubscribeDisposed(Action handler)
        => pluginInterface.GetIpcSubscriber<object>("Penumbra.Disposed").Subscribe(handler);

    public void UnsubscribeDisposed(Action handler)
        => pluginInterface.GetIpcSubscriber<object>("Penumbra.Disposed").Unsubscribe(handler);

    public Dictionary<string, string> GetModList()
        => pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc();

    public List<(Guid Id, string Name)> GetCollections()
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<List<(Guid Id, string Name)>>("Penumbra.GetCollections.V5").InvokeFunc();
        }
        catch (Exception v5Exception)
        {
            PluginService.Log.Debug(v5Exception, "Penumbra.GetCollections.V5 is not available.");
        }

        try
        {
            return pluginInterface.GetIpcSubscriber<List<(Guid Id, string Name)>>("Penumbra.GetCollections").InvokeFunc();
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Penumbra.GetCollections is not available.");
            return [];
        }
    }

    public List<(Guid Id, string Name)> GetCollectionsByIdentifier(string name)
        => pluginInterface.GetIpcSubscriber<string, List<(Guid Id, string Name)>>("Penumbra.GetCollectionsByIdentifier").InvokeFunc(name);

    public (Guid Id, string Name)? FindCollectionByName(string collectionName)
    {
        var matches = new List<(Guid Id, string Name)>();

        foreach (var identifier in CollectionNameMatcher.GetSearchVariants(collectionName))
        {
            foreach (var collection in GetCollectionsByIdentifierSafely(identifier))
                AddUniqueCollection(matches, collection);
        }

        var identifierMatch = PickCollectionMatch(matches, collectionName);
        if (identifierMatch is not null)
            return identifierMatch;

        foreach (var collection in GetCollections())
            AddUniqueCollection(matches, collection);

        return PickCollectionMatch(matches, collectionName);
    }

    public int TrySetMod(Guid collectionId, string modDirectory, string modName, bool enabled)
        => pluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5").InvokeFunc(collectionId, modDirectory, modName, enabled);

    public int TrySetModPriority(Guid collectionId, string modDirectory, string modName, int priority)
        => pluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5").InvokeFunc(collectionId, modDirectory, modName, priority);

    public (int ErrorCode, (Guid Id, string Name)? OldCollection) SetCollectionForObject(int objectIndex, Guid collectionId)
        => pluginInterface.GetIpcSubscriber<int, Guid?, bool, bool, (int ErrorCode, (Guid Id, string Name)? OldCollection)>("Penumbra.SetCollectionForObject.V5")
            .InvokeFunc(objectIndex, collectionId, true, false);

    public (bool Valid, bool Individual, (Guid Id, string Name) Collection) GetCollectionForObject(int objectIndex)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<int, (bool Valid, bool Individual, (Guid Id, string Name) Collection)>("Penumbra.GetCollectionForObject.V5").InvokeFunc(objectIndex);
        }
        catch
        {
            return pluginInterface.GetIpcSubscriber<int, (bool Valid, bool Individual, (Guid Id, string Name) Collection)>("Penumbra.GetCollectionForObject").InvokeFunc(objectIndex);
        }
    }

    public (int ErrorCode, Guid CollectionId) CreateTemporaryCollection(string identity, string name)
        => pluginInterface
            .GetIpcSubscriber<string, string, (int ErrorCode, Guid CollectionId)>("Penumbra.CreateTemporaryCollection.V6")
            .InvokeFunc(identity, name);

    public int DeleteTemporaryCollection(Guid collectionId)
        => pluginInterface
            .GetIpcSubscriber<Guid, int>("Penumbra.DeleteTemporaryCollection.V5")
            .InvokeFunc(collectionId);

    public int AssignTemporaryCollection(Guid collectionId, int objectIndex)
        => pluginInterface
            .GetIpcSubscriber<Guid, int, bool, int>("Penumbra.AssignTemporaryCollection.V5")
            .InvokeFunc(collectionId, objectIndex, true);

    /// <summary>
    /// Turns the mod on in a collection without writing to the player's config.
    ///
    /// "Temporary" describes the settings, not the collection: this works on any
    /// collection, permanent ones included. Penumbra never saves them to disk and
    /// drops them when it reloads, which is what makes it safe to point at a
    /// collection the venue does not own.
    ///
    /// A non-zero <paramref name="key"/> locks them to the holder, so only the
    /// plugin that applied them can take them off again.
    /// </summary>
    public int SetTemporaryModSettings(Guid collectionId, string modDirectory, string modName, int priority, int key)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings =
            new Dictionary<string, IReadOnlyList<string>>();
        return pluginInterface
            .GetIpcSubscriber<
                Guid,
                string,
                string,
                (bool ForceInherit, bool Enabled, int Priority, IReadOnlyDictionary<string, IReadOnlyList<string>> Settings),
                string,
                int,
                int>("Penumbra.SetTemporaryModSettings.V5")
            .InvokeFunc(
                collectionId,
                modDirectory,
                modName,
                (false, true, priority, settings),
                "The Grid venue mod",
                key);
    }

    /// <summary>Removes every temporary setting applied to a collection under one key.</summary>
    public int RemoveAllTemporaryModSettings(Guid collectionId, int key)
        => pluginInterface
            .GetIpcSubscriber<Guid, int, int>("Penumbra.RemoveAllTemporaryModSettings.V5")
            .InvokeFunc(collectionId, key);

    /// <summary>
    /// The collection Penumbra's Base assignment currently points at.
    ///
    /// Base is where everything not drawn as part of a game object resolves —
    /// Penumbra's own description of it is "World, Music, Furniture, baseline for
    /// characters and monsters not specialized". The venue's furniture and the
    /// effects it plays are exactly that, so a collection assigned to the mannequin
    /// never sees those files however correctly it is assigned.
    ///
    /// The gate takes a plain byte rather than ApiCollectionType, which is why
    /// this can ask without referencing Penumbra.Api at all.
    ///
    /// Null covers three cases that all mean the same thing here: the call is
    /// unavailable, nothing is assigned, or the empty collection is. A player who
    /// has put the empty collection on Base has said they want nothing applied
    /// there, and that is not the venue's to overrule.
    /// </summary>
    public (Guid Id, string Name)? GetBaseCollection()
    {
        try
        {
            var assigned = pluginInterface
                .GetIpcSubscriber<byte, (Guid Id, string Name)?>("Penumbra.GetCollection")
                .InvokeFunc(BaseCollectionAssignment);

            return assigned is { } collection && collection.Id != Guid.Empty ? collection : null;
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Penumbra could not report the Base collection.");
            return null;
        }
    }

    /// <summary>
    /// Asks Penumbra to reload indoor housing furniture.
    ///
    /// Decoration loads with the room, so switching the venue's furniture on while
    /// standing in it changes nothing until something reloads it — and
    /// <see cref="RedrawObject"/> only redraws characters.
    ///
    /// Penumbra can do this, but only through its own chat command: the redraw
    /// IPC surface is RedrawObject, RedrawAll and RedrawCollectionMembers, and
    /// RedrawType has no furniture member. Dispatching the command is local and
    /// sends nothing to the server, but it does sit outside the versioned API, so
    /// a failure here is logged and never surfaced as a fault. Penumbra's own
    /// help also calls it indoor-only and warns it "might break some customizable
    /// decoration", which is another reason not to build anything on top of it.
    /// </summary>
    public static void RedrawFurniture()
    {
        if (!PluginService.Framework.IsInFrameworkUpdateThread)
        {
            // Dispatching a command reaches into the game, so it only happens on
            // the framework thread. Skipped rather than queued: every caller is
            // already on that thread, one of them is plugin teardown, and work
            // scheduled from there would run after the plugin is gone. The reload
            // is cosmetic — the settings behind it have already changed.
            PluginService.Log.Debug("Skipped a furniture redraw requested off the framework thread.");
            return;
        }

        try
        {
            if (!PluginService.Commands.ProcessCommand("/penumbra redraw furniture"))
                PluginService.Log.Debug("Penumbra did not accept the furniture redraw command.");
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not ask Penumbra to redraw furniture.");
        }
    }

    public bool IsModEnabled(Guid collectionId, string modDirectory, string modName)
    {
        var (errorCode, settings) = pluginInterface
            .GetIpcSubscriber<Guid, string, string, bool, (int, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings)?)>("Penumbra.GetCurrentModSettings.V5")
            .InvokeFunc(collectionId, modDirectory, modName, false);
        return errorCode == 0 && settings?.Enabled == true;
    }

    public void RedrawObject(int objectIndex)
    {
        var redrawType = GetRedrawType();
        try
        {
            pluginInterface.GetIpcSubscriber<int, object, object>("Penumbra.RedrawObject.V5").InvokeAction(objectIndex, redrawType);
            PluginService.Log.Debug("Requested Penumbra redraw for object index {Index} through RedrawObject.V5.", objectIndex);
            return;
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Typed Penumbra RedrawObject.V5 call failed for object index {Index}; trying integer compatibility mode.", objectIndex);
        }

        try
        {
            pluginInterface.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5").InvokeAction(objectIndex, RedrawTypeRedraw);
            PluginService.Log.Debug("Requested Penumbra redraw for object index {Index} through integer compatibility mode.", objectIndex);
            return;
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Integer Penumbra RedrawObject.V5 call failed for object index {Index}; trying legacy label.", objectIndex);
        }

        pluginInterface.GetIpcSubscriber<int, object, object>("Penumbra.RedrawObjectByIndex").InvokeAction(objectIndex, redrawType);
        PluginService.Log.Debug("Requested Penumbra redraw for object index {Index} through the legacy label.", objectIndex);
    }

    private static object GetRedrawType()
    {
        var redrawType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Penumbra.Api.Enums.RedrawType", throwOnError: false))
            .FirstOrDefault(type => type?.IsEnum == true);

        return redrawType is null ? RedrawTypeRedraw : Enum.ToObject(redrawType, RedrawTypeRedraw);
    }

    private List<(Guid Id, string Name)> GetCollectionsByIdentifierSafely(string name)
    {
        try
        {
            return GetCollectionsByIdentifier(name);
        }
        catch (Exception exception)
        {
            PluginService.Log.Debug(exception, "Could not query Penumbra collection identifier {Identifier}.", name);
            return [];
        }
    }

    private static (Guid Id, string Name)? PickCollectionMatch(List<(Guid Id, string Name)> collections, string collectionName)
    {
        var exact = collections.FirstOrDefault(collection =>
            string.Equals(collection.Name, collectionName, StringComparison.OrdinalIgnoreCase));
        if (exact.Id != Guid.Empty)
            return exact;

        var looseMatches = collections
            .Where(collection => collection.Id != Guid.Empty && CollectionNameMatcher.IsMatch(collection.Name, collectionName))
            .ToList();

        if (looseMatches.Count > 1)
            PluginService.Log.Warning("Multiple Penumbra collections loosely match '{CollectionName}': {Matches}", collectionName, string.Join(", ", looseMatches.Select(collection => collection.Name)));

        if (looseMatches.Count > 0)
            return looseMatches[0];

        return null;
    }

    private static void AddUniqueCollection(List<(Guid Id, string Name)> collections, (Guid Id, string Name) collection)
    {
        if (collection.Id == Guid.Empty || collections.Any(existing => existing.Id == collection.Id))
            return;

        collections.Add(collection);
    }

    private const int RedrawTypeRedraw = 0;

    /// <summary>ApiCollectionType.Default — what Penumbra's Collections tab labels "Base".</summary>
    private const byte BaseCollectionAssignment = 0xE0;
}
