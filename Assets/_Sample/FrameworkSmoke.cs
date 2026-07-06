using Framework;
using Framework.Core;
using Framework.Network;
using Game.Protocol;
using UnityEngine;

/// <summary>
/// 地基冒烟自检（Sample，编译进 Assembly-CSharp，不属于 Framework 程序集）。
/// 验证 GameEntry 各 Manager 已启动、Timer 真正在运转。
///
/// 用法：
/// 1) 新建空场景，放一个空 GameObject；
/// 2) 挂上 GameEntry 与本组件（本组件在 Start 运行，晚于 GameEntry.Awake 的 Manager 初始化）；
/// 3) Resources/AppConfig.asset 已设 EnableHotUpdate=0 / UseNetworkLogin=0（纯框架离线）；
/// 4) 按 Play，Console 出现 "✅ Framework OK" 与 0.5s 后的 Timer 回调即通过。
///
/// 注：本冒烟场景不接 Loading 预制体，GameEntry.Start 会打印一条 _loadingViewPrefab 未赋值的 Error——
/// 属预期（真实项目需拖 Loading 预制体驱动完整启动序列）；Manager 已在 Awake 初始化，不影响本自检。
/// </summary>
public sealed class FrameworkSmoke : MonoBehaviour
{
    private void Start()
    {
        bool ok = GameEntry.Event != null
                  && GameEntry.Timer != null
                  && GameEntry.Resource != null
                  && GameEntry.UI != null
                  && GameEntry.Network != null
                  && GameEntry.RefData != null
                  && GameEntry.Audio != null
                  && GameEntry.Scene != null
                  && GameEntry.Auth != null;

        if (!ok)
        {
            GameLog.Error("[FrameworkSmoke] ❌ 部分 Manager 未初始化，确认场景中有 GameEntry 且其 Awake 先于本组件执行");
            return;
        }

        // 用 Timer 排一个一次性回调，证明 Manager 真正在运转（非仅构造成功）。
        GameEntry.Timer.AddTimer(
            () => GameLog.Log("[FrameworkSmoke] ✅ Timer 0.5s 回调触发，地基运转正常"),
            0.5f);

        GameLog.Log("[FrameworkSmoke] ✅ Framework OK —— 所有 Manager 已启动（Event/Timer/Resource/UI/Network/RefData/Audio/Scene/Auth）");

        ProtobufRoundTrip();
    }

    /// <summary>
    /// Google.Protobuf 收发链路自检：走框架完整路径（ProtobufUtil 序列化 → MessagePacket 组包 → 解包 → 反序列化），
    /// 验证生成协议、路由号（GetMainId/GetSubId）与二进制往返均正常，不依赖真实网络连接。
    /// </summary>
    private void ProtobufRoundTrip()
    {
        var request = new GC2GS_001_001_HeartbeatRequest { ClientTime = 1234567890123, SequenceId = 7 };

        byte[] payload = ProtobufUtil.Serialize(request);
        byte[] packet = MessagePacket.Pack(request, payload, seqId: 42);

        if (!MessagePacket.Unpack(packet, out byte mainId, out byte subId, out ushort seqId, out byte[] body))
        {
            GameLog.Error("[FrameworkSmoke] ❌ Protobuf 往返失败：消息包解析失败");
            return;
        }

        var back = ProtobufUtil.Deserialize<GC2GS_001_001_HeartbeatRequest>(body);
        bool ok = mainId == request.GetMainId()
                  && subId == request.GetSubId()
                  && seqId == 42
                  && back.ClientTime == request.ClientTime
                  && back.SequenceId == request.SequenceId;

        if (ok)
        {
            GameLog.Log($"[FrameworkSmoke] ✅ Protobuf 往返正常（main={mainId}, sub={subId}, seq={seqId}, ClientTime={back.ClientTime}, Seq={back.SequenceId}）");
        }
        else
        {
            GameLog.Error("[FrameworkSmoke] ❌ Protobuf 往返字段不一致");
        }
    }
}
