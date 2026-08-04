using System;
using Unity.Profiling;
using UnityEngine;

// 挂到场景里任意物体上，Play 后左上角显示 GPU 时间代理指标的滑动窗口中位数。
// 用途：splat 切分方案的 A/B 实验（见 docs/splat-asset-split-plan.md §0.3）。
// GPU-bound 时主线程绝大部分时间在等 present，所以 PresentWait 就是 GPU 时间的镜像。
public class GpuTimeProbe : MonoBehaviour
{
    [Tooltip("统计窗口帧数。太小抖动大，太大对改动反应慢。")]
    [SerializeField] int m_WindowSize = 120;
    [Tooltip("按此键把当前读数打到 Console，方便复制进实验记录。")]
    [SerializeField] KeyCode m_LogKey = KeyCode.L;
    [SerializeField] KeyCode m_ResetKey = KeyCode.R;

    ProfilerRecorder m_PresentWait;
    ProfilerRecorder m_MainThread;

    double[] m_PresentSamples;
    double[] m_FrameSamples;
    int m_Count;
    int m_Head;

    void OnEnable()
    {
        m_PresentSamples = new double[m_WindowSize];
        m_FrameSamples = new double[m_WindowSize];
        m_Count = 0;
        m_Head = 0;

        // 标记名与 Profiler Hierarchy 里显示的一致。若某个 recorder 无效，UI 上会标 n/a。
        m_PresentWait = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread");
        m_MainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
    }

    void OnDisable()
    {
        m_PresentWait.Dispose();
        m_MainThread.Dispose();
    }

    void Update()
    {
        if (Input.GetKeyDown(m_ResetKey))
        {
            m_Count = 0;
            m_Head = 0;
        }

        m_PresentSamples[m_Head] = m_PresentWait.Valid ? m_PresentWait.LastValue * 1e-6 : 0.0;
        m_FrameSamples[m_Head] = m_MainThread.Valid ? m_MainThread.LastValue * 1e-6 : Time.unscaledDeltaTime * 1000.0;
        m_Head = (m_Head + 1) % m_WindowSize;
        if (m_Count < m_WindowSize)
            ++m_Count;

        if (Input.GetKeyDown(m_LogKey))
        {
            Debug.Log($"[GpuTimeProbe] n={m_Count}  PresentWait median={Median(m_PresentSamples):F2}ms  " +
                      $"Frame median={Median(m_FrameSamples):F2}ms");
        }
    }

    double Median(double[] buf)
    {
        if (m_Count == 0)
            return 0.0;
        var tmp = new double[m_Count];
        Array.Copy(buf, tmp, m_Count);
        Array.Sort(tmp);
        return m_Count % 2 == 1 ? tmp[m_Count / 2] : (tmp[m_Count / 2 - 1] + tmp[m_Count / 2]) * 0.5;
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        style.normal.textColor = Color.white;

        string present = m_PresentWait.Valid ? $"{Median(m_PresentSamples):F2} ms" : "n/a";
        string frame = $"{Median(m_FrameSamples):F2} ms";

        GUI.Box(new Rect(8, 8, 360, 108), GUIContent.none);
        GUI.Label(new Rect(16, 12, 350, 30), $"PresentWait (GPU 代理): {present}", style);
        GUI.Label(new Rect(16, 42, 350, 30), $"Main Thread: {frame}", style);
        GUI.Label(new Rect(16, 76, 350, 26),
            $"窗口 {m_Count}/{m_WindowSize}    [{m_ResetKey}] 重置  [{m_LogKey}] 打日志");
    }
}
