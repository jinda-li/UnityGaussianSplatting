using System;
using System.Collections;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;

// 自动跑一轮 A/B 实验，每个配置持续固定秒数，并在 logcat 打 [GSEXP] 标记。
// 配套分析：用 adb logcat 抓 VrApi 的 App= 值，按标记时间段切分求中位数。
// 背景与决策表见 docs/splat-asset-split-plan.md §0.3。
//
// 用法：挂到场景任意物体上 -> Build & Run -> 站定不动约 2 分钟 -> 从 logcat 取数。
public class SplatPerfExperiment : MonoBehaviour
{
    [Tooltip("每个配置的持续时间。前 m_SettleSeconds 秒的数据在分析时应丢弃。")]
    [SerializeField] float m_SecondsPerConfig = 20f;
    [Tooltip("切换配置后的稳定期，仅用于在日志里标注，不影响执行。")]
    [SerializeField] float m_SettleSeconds = 5f;
    [SerializeField] bool m_AutoStart = true;

    const string kTag = "[GSEXP]";

    GaussianSplatRenderer[] m_Renderers;
    float m_OrigViewportScale;
    int[] m_OrigSortNthFrame;
    int[] m_OrigSHOrder;
    float[] m_OrigSplatScale;

    class Config
    {
        public string name;
        public Action apply;
        public Config(string n, Action a) { name = n; apply = a; }
    }

    void Start()
    {
        m_Renderers = FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);
        if (m_Renderers.Length == 0)
        {
            Debug.LogError($"{kTag} no GaussianSplatRenderer found, experiment aborted");
            enabled = false;
            return;
        }

        int n = m_Renderers.Length;
        m_OrigSortNthFrame = new int[n];
        m_OrigSHOrder = new int[n];
        m_OrigSplatScale = new float[n];
        long totalSplats = 0;
        for (int i = 0; i < n; ++i)
        {
            m_OrigSortNthFrame[i] = m_Renderers[i].m_SortNthFrame;
            m_OrigSHOrder[i] = m_Renderers[i].m_SHOrder;
            m_OrigSplatScale[i] = m_Renderers[i].m_SplatScale;
            if (m_Renderers[i].asset != null)
                totalSplats += m_Renderers[i].asset.splatCount;
        }
        m_OrigViewportScale = XRSettings.renderViewportScale;

        Debug.Log($"{kTag} INIT renderers={n} totalSplats={totalSplats} " +
                  $"viewportScale={m_OrigViewportScale:F3} " +
                  $"eyeTexRes={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");

        if (m_AutoStart)
            StartCoroutine(RunAll());
    }

    void OnDisable()
    {
        RestoreAll();
    }

    void RestoreAll()
    {
        if (m_Renderers == null)
            return;
        XRSettings.renderViewportScale = m_OrigViewportScale;
        for (int i = 0; i < m_Renderers.Length; ++i)
        {
            if (m_Renderers[i] == null)
                continue;
            m_Renderers[i].m_SortNthFrame = m_OrigSortNthFrame[i];
            m_Renderers[i].m_SHOrder = m_OrigSHOrder[i];
            m_Renderers[i].m_SplatScale = m_OrigSplatScale[i];
        }
    }

    void ForEach(Action<GaussianSplatRenderer, int> act)
    {
        for (int i = 0; i < m_Renderers.Length; ++i)
            if (m_Renderers[i] != null)
                act(m_Renderers[i], i);
    }

    IEnumerator RunAll()
    {
        var configs = new List<Config>
        {
            // 基线：全部还原
            new Config("baseline", RestoreAll),

            // 纯 fill-rate 变量：像素量变 1/4，splat 数/排序量/带宽读取量都不变
            new Config("viewport_0.5", () => {
                RestoreAll();
                XRSettings.renderViewportScale = 0.5f;
            }),

            // 排序变量：排序频率降到 1/8
            new Config("sortNth_8", () => {
                RestoreAll();
                ForEach((r, i) => r.m_SortNthFrame = 8);
            }),

            // 排序频率的中间档，用于找画质可接受的最大值
            new Config("sortNth_2", () => {
                RestoreAll();
                ForEach((r, i) => r.m_SortNthFrame = 2);
            }),
            new Config("sortNth_4", () => {
                RestoreAll();
                ForEach((r, i) => r.m_SortNthFrame = 4);
            }),

            // 注意：不要加 m_SplatScale 实验——该参数关系画面效果，不可改动。

            // 再测一次基线：检测热节流/漂移。若与第一次基线差异大，本轮数据不可信。
            new Config("baseline_end", RestoreAll),
        };

        Debug.Log($"{kTag} RUN_START configs={configs.Count} secondsPerConfig={m_SecondsPerConfig}");

        foreach (var cfg in configs)
        {
            cfg.apply();
            // 等一帧让改动落到渲染上，再打 BEGIN，保证标记时间点之后的数据都属于本配置
            yield return null;
            Debug.Log($"{kTag} BEGIN {cfg.name} settle={m_SettleSeconds}s " +
                      $"viewportScale={XRSettings.renderViewportScale:F3} " +
                      $"sortNth={m_Renderers[0].m_SortNthFrame} " +
                      $"shOrder={m_Renderers[0].m_SHOrder} " +
                      $"splatScale={m_Renderers[0].m_SplatScale:F2}");

            yield return new WaitForSecondsRealtime(m_SecondsPerConfig);

            Debug.Log($"{kTag} END {cfg.name}");
        }

        RestoreAll();
        Debug.Log($"{kTag} RUN_DONE");
    }
}
