using System;
using System.Collections.Generic;
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

    public Dictionary<string, string> GetModList()
        => pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList").InvokeFunc();

    public List<(Guid Id, string Name)> GetCollectionsByIdentifier(string name)
        => pluginInterface.GetIpcSubscriber<string, List<(Guid Id, string Name)>>("Penumbra.GetCollectionsByIdentifier").InvokeFunc(name);

    public int TrySetMod(Guid collectionId, string modDirectory, string modName, bool enabled)
        => pluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5").InvokeFunc(collectionId, modDirectory, modName, enabled);

    public int TrySetModPriority(Guid collectionId, string modDirectory, string modName, int priority)
        => pluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5").InvokeFunc(collectionId, modDirectory, modName, priority);

    public (int ErrorCode, (Guid Id, string Name)? OldCollection) SetCollectionForObject(int objectIndex, Guid collectionId)
        => pluginInterface.GetIpcSubscriber<int, Guid?, bool, bool, (int ErrorCode, (Guid Id, string Name)? OldCollection)>("Penumbra.SetCollectionForObject.V5")
            .InvokeFunc(objectIndex, collectionId, true, false);

    public (int ErrorCode, (Guid Id, string Name)? OldCollection) SetCollection(byte collectionType, Guid collectionId)
        => pluginInterface.GetIpcSubscriber<byte, Guid?, bool, bool, (int ErrorCode, (Guid Id, string Name)? OldCollection)>("Penumbra.SetCollection")
            .InvokeFunc(collectionType, collectionId, true, false);
}
