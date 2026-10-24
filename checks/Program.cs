using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FrooxEngine;
using HarmonyLib;
using LiteNetLib;
using LnlConnectionFix;
using ResoniteModLoader;

internal static class Program
{
    private static int _checks;

    private static int Main(string[] args)
    {
        string game = Path.GetFullPath(args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Resonite");
        string rml = Path.GetFullPath(args.Length > 1 ? args[1] : game);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? file = new[] { game, Path.Combine(rml, "Libraries"), Path.Combine(rml, "rml_libs") }
                .Select(dir => Path.Combine(dir, name.Name + ".dll")).FirstOrDefault(File.Exists);
            return file == null ? null : context.LoadFromAssemblyPath(file);
        };
        try
        {
            // RMLが作成する空のライブラリフォルダーは検査のビルド出力内へ置く。
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            Run();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var plugin = new Plugin();
        Check(typeof(Plugin).Assembly.GetTypes().Count(t => !t.IsAbstract && typeof(ResoniteMod).IsAssignableFrom(t)) == 1,
            "RMLが検出するModの実装は1個だけ");
        Check(plugin.Name == "LnlConnectionFix" && plugin.Author == "markn2000" && plugin.Version == "0.1.0",
            "RMLのMod情報");
        Check(typeof(Plugin).Assembly.GetName().Name == "LnlConnectionFix"
            && !typeof(Plugin).Assembly.GetReferencedAssemblies().Any(a => a.Name!.StartsWith("BepInEx", StringComparison.Ordinal)),
            "DLL名を統一し、BepInExへの依存を持たない");
        var constructor = typeof(LNL_Listener).GetConstructors().Single();
        var original = PatchProcessor.GetOriginalInstructions(constructor);
        // nullのWorld参照で止まる前にソケット開始がないことを検査する。
        int worldRead = original.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType == typeof(World) && m.Name == "get_Engine");
        Check(worldRead > 0 && !original.Take(worldRead).Any(i =>
            i.operand is MethodInfo m && m.DeclaringType == typeof(NetManager) && m.Name == "Start"), "検査用コンストラクターの停止位置");
        ExpectRejected([]);
        var wrongCreation = original.Select(i => new CodeInstruction(i)).ToList();
        wrongCreation[1].opcode = OpCodes.Nop;
        ExpectRejected(wrongCreation);
        var duplicateStore = original.Select(i => new CodeInstruction(i)).ToList();
        duplicateStore.Add(new CodeInstruction(original[2]));
        ExpectRejected(duplicateStore);

        CheckCallbacks(false);
        var harmony = new Harmony("markn2000.LnlConnectionFix");
        try
        {
            plugin.OnEngineInit();
            Check(Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m)?.Owners.Contains(harmony.Id) == true) == 1,
                "パッチ対象はコンストラクター1か所だけ");
            CheckCallbacks(true);
            CheckReusedEndpoint();
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
        var restored = CreateListener();
        Check(!ReferenceEquals(Peers(restored).Comparer, ReferenceEqualityComparer.Instance), "パッチ解除後の辞書生成が元に戻る");
        Console.WriteLine($"PASS: {_checks}検査。RMLのOnEngineInit、実Harmonyパッチ、受信・統計通知・切断、再参加、適用不一致を確認。");
        Console.WriteLine("ゲーム・ソケットは未起動。WorldのUserLeftと画面の統計値は実機確認が必要です。");
    }

    private static void ExpectRejected(IEnumerable<CodeInstruction> instructions)
    {
        try { Plugin.Transpile(instructions).ToList(); }
        catch (InvalidOperationException) { _checks++; return; }
        throw new InvalidOperationException("未対応の初期化形式を拒否しませんでした。");
    }

    private static LNL_Listener CreateListener()
    {
        var listener = (LNL_Listener)RuntimeHelpers.GetUninitializedObject(typeof(LNL_Listener));
        // 実コンストラクターの初期化を実行し、Worldを渡さず通信開始前に止める。
        try { typeof(LNL_Listener).GetConstructors().Single().Invoke(listener, ["LnlConnectionFix.Checks", (ushort)0, IPAddress.Any, null, null]); }
        catch (TargetInvocationException error) when (error.InnerException is NullReferenceException) { }
        Check(Peers(listener) != null && !Manager(listener).IsRunning, "辞書が生成され、ネットワークが起動していない");
        return listener;
    }

    private static Dictionary<NetPeer, LNL_Peer> Peers(LNL_Listener listener) =>
        (Dictionary<NetPeer, LNL_Peer>)AccessTools.DeclaredField(typeof(LNL_Listener), "_peers").GetValue(listener)!;

    private static NetManager Manager(LNL_Listener listener) =>
        (NetManager)AccessTools.DeclaredField(typeof(LNL_Listener), "_server").GetValue(listener)!;

    private static NetPeer NewPeer(NetManager manager) => (NetPeer)AccessTools.Constructor(typeof(NetPeer),
        [typeof(NetManager), typeof(IPEndPoint), typeof(int)]).Invoke([manager, Endpoint("192.0.2.10", 40000), 7]);

    private static IPEndPoint Endpoint(string address, int port) => new(IPAddress.Parse(address), port);

    private static void ChangeEndpoint(NetPeer peer, IPEndPoint endpoint)
    {
        AccessTools.DeclaredMethod(typeof(NetPeer), "InitiateEndPointChange").Invoke(peer, null);
        AccessTools.DeclaredMethod(typeof(NetPeer), "FinishEndPointChange").Invoke(peer, [endpoint]);
    }

    private static void CheckCallbacks(bool patched)
    {
        IPEndPoint[][] changes = [
            [],
            [Endpoint("192.0.2.10", 40001)],
            [Endpoint("203.0.113.20", 40000)],
            [Endpoint("203.0.113.20", 40001)],
            [Endpoint("192.0.2.10", 40001), Endpoint("198.51.100.30", 40002)],
            [Endpoint("192.0.2.10", 40001), Endpoint("192.0.2.10", 40000)]
        ];
        foreach (var endpoints in changes)
        foreach (var reason in new[] { DisconnectReason.RemoteConnectionClose, DisconnectReason.Timeout })
        {
            var listener = CreateListener();
            Check(ReferenceEquals(Peers(listener).Comparer, ReferenceEqualityComparer.Instance) == patched, "実コンストラクターの辞書比較方法");
            var peer = NewPeer(Manager(listener));
            int originalHash = peer.GetHashCode();
            listener.OnPeerConnected(peer);
            var wrapper = Peers(listener)[peer];
            int closed = 0;
            wrapper.Closed += connection => { Check(ReferenceEquals(connection, wrapper), "Closedの接続対象"); closed++; };
            CheckReceiveAndStats(listener, peer, wrapper, true);
            foreach (var endpoint in endpoints)
            {
                ChangeEndpoint(peer, endpoint);
                CheckReceiveAndStats(listener, peer, wrapper, patched || peer.GetHashCode() == originalHash);
            }
            bool shouldClose = patched || peer.GetHashCode() == originalHash;
            var info = new DisconnectInfo { Reason = reason };
            listener.OnPeerDisconnected(peer, info);
            listener.OnPeerDisconnected(peer, info);
            Check(closed == (shouldClose ? 1 : 0), "通常切断・Timeout・重複通知時のClosed回数");
            Check(wrapper.IsOpen == !shouldClose && Peers(listener).Count == (shouldClose ? 0 : 1), "切断後の接続状態と辞書の削除");
            Check(!Manager(listener).IsRunning, "検査中にソケットを起動しない");
        }
    }

    private static void CheckReceiveAndStats(LNL_Listener listener, NetPeer peer, LNL_Peer wrapper, bool expected)
    {
        RawInMessage? receivedByListener = null;
        RawInMessage? receivedByPeer = null;
        int stats = 0;
        DataEvent onListenerData = message => receivedByListener = message;
        DataEvent onPeerData = message => receivedByPeer = message;
        StatsEvent onStats = (connection, update) =>
        {
            Check(ReferenceEquals(connection, wrapper) && update != null, "通信統計の更新先と更新処理が通知される");
            stats++;
        };
        listener.NewData += onListenerData;
        wrapper.NewData += onPeerData;
        listener.StatsUpdated += onStats;
        var eventType = typeof(NetPeer).Assembly.GetType("LiteNetLib.NetEvent", throwOnError: true)!;
        var netEvent = Activator.CreateInstance(eventType, [Manager(listener)])!;
        var reader = (NetPacketReader)eventType.GetField("DataReader")!.GetValue(netEvent)!;
        byte[] bytes = [0xFF, 1, 2, 3];
        reader.SetSource(bytes, 1, bytes.Length);
        ulong before = wrapper.ReceivedBytes;
        try
        {
            listener.OnNetworkReceive(peer, reader, 0, DeliveryMethod.ReliableOrdered);
            listener.OnNetworkLatencyUpdate(peer, 42);
            Check((receivedByListener != null) == expected && (receivedByPeer != null) == expected, "受信がListenerと接続の両方へ届く");
            Check(stats == (expected ? 1 : 0), "通信統計の更新通知回数");
            Check((wrapper.ReceivedBytes > before) == expected, "受信バイト数の更新");
            if (expected)
            {
                Check(ReferenceEquals(receivedByListener, receivedByPeer), "同じ受信メッセージが渡される");
                Check(ReferenceEquals(receivedByListener!.Sender, wrapper) && ReferenceEquals(receivedByListener.Data, bytes)
                    && receivedByListener.Offset == 1 && receivedByListener.Size == 3, "受信元・データ・オフセット・サイズを保持");
            }
        }
        finally
        {
            listener.NewData -= onListenerData;
            wrapper.NewData -= onPeerData;
            listener.StatsUpdated -= onStats;
            if (receivedByListener != null) receivedByListener.Dispose();
            else reader.Recycle();
        }
    }

    private static void CheckReusedEndpoint()
    {
        var listener = CreateListener();
        var oldPeer = NewPeer(Manager(listener));
        listener.OnPeerConnected(oldPeer);
        var info = new DisconnectInfo { Reason = DisconnectReason.RemoteConnectionClose };
        listener.OnPeerDisconnected(oldPeer, info);
        var newPeer = NewPeer(Manager(listener));
        listener.OnPeerConnected(newPeer);
        var wrapper = Peers(listener)[newPeer];
        listener.OnPeerDisconnected(oldPeer, info);
        Check(wrapper.IsOpen && Peers(listener).Count == 1, "同じエンドポイント・IDの再参加を古い切断通知で閉じない");
        CheckReceiveAndStats(listener, newPeer, wrapper, true);
        listener.OnPeerDisconnected(newPeer, info);
        Check(!wrapper.IsOpen && Peers(listener).Count == 0, "再参加した接続も正常に閉じる");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
