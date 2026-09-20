using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Runtime;
using R2B.Editor.GaussianCollision;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Trogir.EditorTools
{
    // Builds Trogir.unity: the SuperSplat capture of Trogir's old town, with the
    // project's VR locomotion player standing in it on a collision proxy.
    //
    // The capture is "Get lost in the alleys of historical Trogir, Croatia
    // (XGRIDS PortalCam)" by Paolo Tosolini, CC BY 4.0,
    // https://superspl.at/scene/14bac5b2 - see Assets/Trogir/CREDITS.md.
    //
    // Two frames matter here and they are not the same one:
    //   file   - raw PLY coordinates, which is also GaussianSplatAsset local space
    //   engine - what splat-transform and the SuperSplat viewer call world:
    //            the file frame rotated 180 degrees about Z (x -> -x, y -> -y)
    // The splat renderer therefore carries a 180 degree Z rotation, which puts the
    // asset into the engine frame, and Unity world coordinates then read the same
    // as the numbers in the online viewer. That is what makes the viewer's own
    // start camera usable as a spawn point. The collision GLB comes out of
    // splat-transform in the engine frame too, so GaussianSplatCoords rotates it
    // back into file space before it is parented under the renderer.
    public static class TrogirSceneBuilder
    {
        public const string k_Dir = "Assets/Trogir";
        public const string k_ScenePath = k_Dir + "/Trogir.unity";
        const string k_GaussianAssetDir = "Assets/GaussianAssets";
        const string k_CollisionDir = k_GaussianAssetDir + "/Collision";
        const string k_PlayerPrefab =
            "Assets/VRPlayerLocomotion/VR Player Locomotion Survival Character.prefab";
        const string k_SimulatorPrefab =
            "Assets/Samples/XR Interaction Toolkit/3.3.2/XR Device Simulator/XR Device Simulator.prefab";

        const string k_ShotDir = "Assets/Screenshots/Trogir";
        const string k_ReportPath = k_ShotDir + "/walk-report.txt";
        const string k_LocoShotDir = "Assets/Screenshots/TrogirLocomotion";
        const string k_LocoReportPath = k_LocoShotDir + "/locomotion-report.txt";
        const string k_WatchKey = "trogir.probe.watching";
        const string k_DeadlineKey = "trogir.probe.deadline";
        const string k_ReportKey = "trogir.probe.report";

        const string k_SplatObjectName = "TrogirSplat";
        const string k_ProxyName = "CollisionProxy";

        // The viewer's own opening shot, in engine coordinates - an alley near the
        // middle of the capture with walls on both sides, which is the whole point
        // of walking here. The y is the alley floor the probe measured (-0.45)
        // plus enough clearance that the character drops onto it rather than
        // starting inside it.
        static readonly Vector3 k_Spawn = new Vector3(14.77f, -0.3f, 42.51f);

        // Facing down the alley rather than down +Z. The locomotion moves relative
        // to where the head is looking, and with no headset attached the head
        // starts wherever the rig does - spawning across the alley would mean the
        // first step is into a wall.
        static readonly Quaternion k_SpawnLook = Quaternion.Euler(0f, 63.4f, 0f);

        // Traced off a top-down render of the capture: up the alley the spawn
        // faces, back down it, then round the corner into the side alley that
        // runs north. The corner is the interesting part - a route that only
        // ever goes straight would pass even if the proxy were a flat plane.
        // Waypoint y is ignored; the probe falls onto whatever floor it finds.
        static readonly Vector3[] k_Route =
        {
            new Vector3(18.0f, 0f, 44.2f),
            new Vector3(22.0f, 0f, 46.2f),
            new Vector3(24.5f, 0f, 47.3f),
            new Vector3(18.0f, 0f, 44.2f),
            new Vector3(13.6f, 0f, 47.0f),
            new Vector3(12.8f, 0f, 52.0f),
        };

        static string PlyPath => Env("TROGIR_PLY", DefaultPly());
        static string GlbPath => Env("TROGIR_GLB", DefaultGlb());

        static string DefaultPly() => Path.Combine(ScratchDir(), "Trogir_lod2.ply");
        static string DefaultGlb() => Path.Combine(ScratchDir(), "trogir_full.collision.glb");

        static string ScratchDir() => Environment.GetEnvironmentVariable("TROGIR_SCRATCH") ?? "";

        static string Env(string key, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        [MenuItem("R2B/Trogir/1 - Import Splat Asset")]
        public static void ImportSplat()
        {
            string ply = PlyPath;
            if (!File.Exists(ply))
                throw new FileNotFoundException(
                    $"[trogir] No PLY at '{ply}'. Set TROGIR_PLY or TROGIR_SCRATCH.", ply);

            Directory.CreateDirectory(k_GaussianAssetDir);

            var type = typeof(GaussianSplatting.Editor.GaussianSplatAssetCreator);
            var creator = ScriptableObject.CreateInstance(type);
            try
            {
                SetField(creator, "m_InputFile", ply);
                SetField(creator, "m_ImportCameras", false);
                SetField(creator, "m_OutputFolder", k_GaussianAssetDir);
                // The capture carries no spherical harmonics (SuperSplat's SOG drops
                // them), so the SH format only decides how much room is wasted on
                // zeroes - cluster it down to nothing and spend the bits on position.
                SetField(creator, "m_FormatPos", GaussianSplatAsset.VectorFormat.Norm16);
                SetField(creator, "m_FormatScale", GaussianSplatAsset.VectorFormat.Norm16);
                SetField(creator, "m_FormatColor", GaussianSplatAsset.ColorFormat.Norm8x4);
                SetField(creator, "m_FormatSH", GaussianSplatAsset.SHFormat.Cluster4k);

                var create = type.GetMethod("CreateAsset",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (create == null)
                    throw new MissingMethodException(type.FullName, "CreateAsset");
                create.Invoke(creator, null);

                string error = GetField(creator, "m_ErrorMessage") as string;
                if (!string.IsNullOrWhiteSpace(error))
                    throw new InvalidOperationException("[trogir] import failed: " + error);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(creator);
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var asset = LoadSplatAsset();
            Debug.Log($"[trogir] imported {AssetDatabase.GetAssetPath(asset)}: " +
                      $"{asset.splatCount:N0} splats, bounds {asset.boundsMin} .. {asset.boundsMax}");
        }

        [MenuItem("R2B/Trogir/2 - Build Scene")]
        public static void BuildScene()
        {
            var splatAsset = LoadSplatAsset();
            Directory.CreateDirectory(k_Dir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.color = new Color(1f, 0.97f, 0.92f);
            sun.shadows = LightShadows.None; // nothing in the scene casts or receives
            sunGo.transform.rotation = Quaternion.Euler(50f, 150f, 0f);

            var splatGo = new GameObject(k_SplatObjectName);
            splatGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 0f, 180f));
            var renderer = splatGo.AddComponent<GaussianSplatRenderer>();
            renderer.m_Asset = splatAsset;

            var player = (GameObject)PrefabUtility.InstantiatePrefab(
                LoadPrefab(k_PlayerPrefab));
            player.name = "Player";
            player.transform.SetPositionAndRotation(k_Spawn, k_SpawnLook);

            var simulator = (GameObject)PrefabUtility.InstantiatePrefab(
                LoadPrefab(k_SimulatorPrefab));
            simulator.name = "XR Device Simulator";

            var body = player.GetComponentInChildren<CharacterController>(true);
            if (body == null)
                Debug.LogWarning("[trogir] the player prefab has no CharacterController - " +
                                 "the walk probe will have nothing to move");
            else
                body.transform.SetPositionAndRotation(k_Spawn, k_SpawnLook);

            // The probe drives the capsule directly and needs a camera it is allowed
            // to move, so it gets its own rather than fighting the XR rig's. Both
            // it and the probe are off in the saved scene: pressing Play should
            // give you the headset and the locomotion rig, not a scripted tour
            // that switches the player's own components off. RunWalkProbe turns
            // them on for its own run only, without saving.
            var probeCamGo = new GameObject("Probe Camera");
            var probeCam = probeCamGo.AddComponent<Camera>();
            probeCam.nearClipPlane = 0.05f;
            probeCam.farClipPlane = 400f;
            probeCam.fieldOfView = 75f;
            probeCam.clearFlags = CameraClearFlags.SolidColor;
            probeCam.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            probeCamGo.transform.position = k_Spawn + Vector3.up * 1.6f;
            probeCamGo.SetActive(false);

            var probeGo = new GameObject("Walk Probe");
            var probe = probeGo.AddComponent<TrogirWalkProbe>();
            probe.m_Body = body;
            probe.m_Camera = probeCam;
            probe.m_Waypoints = k_Route;
            probe.m_RunOnStart = false;
            probeGo.SetActive(false);

            var locoGo = new GameObject("Locomotion Probe");
            var loco = locoGo.AddComponent<TrogirLocomotionProbe>();
            loco.m_Waypoints = k_Route;
            loco.m_Camera = player.GetComponentInChildren<Camera>(true);
            loco.m_RunOnStart = false;
            locoGo.SetActive(false);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, k_ScenePath);
            Debug.Log($"[trogir] wrote {k_ScenePath}, spawn {k_Spawn} facing {k_SpawnLook.eulerAngles.y:0.0}, " +
                      $"rig camera {(loco.m_Camera != null ? loco.m_Camera.name : "<none>")}");
        }

        [MenuItem("R2B/Trogir/3 - Apply Collision Mesh")]
        public static void ApplyCollision()
        {
            string glb = GlbPath;
            if (!File.Exists(glb))
                throw new FileNotFoundException(
                    $"[trogir] No collision GLB at '{glb}'. Set TROGIR_GLB or TROGIR_SCRATCH.", glb);

            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            var renderer = UnityEngine.Object.FindFirstObjectByType<GaussianSplatRenderer>();
            if (renderer == null)
                throw new InvalidOperationException(
                    $"[trogir] {k_ScenePath} has no GaussianSplatRenderer - run Build Scene first.");

            var mesh = GlbMeshLoader.LoadFirstMesh(glb);
            if (mesh.vertexCount == 0)
                throw new InvalidOperationException($"[trogir] {glb} holds an empty mesh.");

            GaussianSplatCoords.TransformMeshEngineToFile(mesh);

            if (!AssetDatabase.IsValidFolder(k_CollisionDir))
                AssetDatabase.CreateFolder(k_GaussianAssetDir, "Collision");

            string meshPath = $"{k_CollisionDir}/{renderer.m_Asset.name}_collision.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();

            Transform proxyT = renderer.transform.Find(k_ProxyName);
            GameObject proxy;
            if (proxyT == null)
            {
                proxy = new GameObject(k_ProxyName);
                proxy.transform.SetParent(renderer.transform, false);
            }
            else
            {
                proxy = proxyT.gameObject;
            }

            if (!proxy.TryGetComponent(out MeshFilter mf))
                mf = proxy.AddComponent<MeshFilter>();
            if (!proxy.TryGetComponent(out MeshCollider mc))
                mc = proxy.AddComponent<MeshCollider>();
            // PhysX's fast midphase only indexes 2,097,152 triangles, and this mesh
            // is well past that; left on, collisions against the triangles beyond
            // that point can simply be missed. Set before the mesh, so the cook that
            // assigning it kicks off is the one that counts.
            mc.cookingOptions &= ~MeshColliderCookingOptions.UseFastMidphase;
            mf.sharedMesh = mesh;
            mc.sharedMesh = mesh;
            mc.convex = false;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, k_ScenePath);

            Bounds world = GaussianSplatCoords.GetMeshWorldBounds(mesh, renderer.transform);
            Debug.Log($"[trogir] collision {meshPath}: {mesh.triangles.Length / 3:N0} tris, " +
                      $"world bounds {world.min} .. {world.max}");
        }

        // Batch entry points: open the scene, play it once, and quit when the
        // chosen probe has written its report.
        //
        // Entering play mode reloads the domain, which drops any update callback
        // registered before it, so the watch has to be re-hooked on the other side
        // - hence the SessionState flags rather than plain statics.
        public static void RunWalkProbe() => RunProbe(k_ReportPath, arm: scene =>
        {
            var probe = UnityEngine.Object.FindFirstObjectByType<TrogirWalkProbe>(
                FindObjectsInactive.Include);
            if (probe == null)
                throw new InvalidOperationException(
                    $"[trogir] {k_ScenePath} has no TrogirWalkProbe - run Build Scene first.");
            probe.gameObject.SetActive(true);
            probe.m_RunOnStart = true;
            if (probe.m_Camera != null)
                probe.m_Camera.gameObject.SetActive(true);
        });

        public static void RunLocomotionProbe() => RunProbe(k_LocoReportPath, arm: scene =>
        {
            var probe = UnityEngine.Object.FindFirstObjectByType<TrogirLocomotionProbe>(
                FindObjectsInactive.Include);
            if (probe == null)
                throw new InvalidOperationException(
                    $"[trogir] {k_ScenePath} has no TrogirLocomotionProbe - run Build Scene first.");
            probe.gameObject.SetActive(true);
            probe.m_RunOnStart = true;
        });

        static void RunProbe(string reportPath, Action<UnityEngine.SceneManagement.Scene> arm)
        {
            if (File.Exists(reportPath))
                File.Delete(reportPath);

            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);

            // Armed in memory only - the scene on disk stays in its "press Play and
            // walk around yourself" state.
            arm(scene);

            SessionState.SetString(k_ReportKey, reportPath);
            SessionState.SetBool(k_WatchKey, true);
            SessionState.SetFloat(k_DeadlineKey, (float)EditorApplication.timeSinceStartup + 600f);
            EditorApplication.update += WatchProbe;
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void RehookProbeWatch()
        {
            if (SessionState.GetBool(k_WatchKey, false))
                EditorApplication.update += WatchProbe;
        }

        static void WatchProbe()
        {
            string reportPath = SessionState.GetString(k_ReportKey, k_ReportPath);
            bool done = File.Exists(reportPath);
            bool expired = EditorApplication.timeSinceStartup > SessionState.GetFloat(k_DeadlineKey, 0f);
            if (!done && !expired)
                return;

            EditorApplication.update -= WatchProbe;
            SessionState.SetBool(k_WatchKey, false);

            if (done)
                Debug.Log("[trogir] " + File.ReadAllText(reportPath));
            else
                Debug.LogError($"[trogir] the probe never wrote {reportPath}");

            EditorApplication.Exit(done ? 0 : 2);
        }

        [MenuItem("R2B/Trogir/Build Everything")]
        public static void BuildEverything()
        {
            ImportSplat();
            BuildScene();
            ApplyCollision();
        }

        static GaussianSplatAsset LoadSplatAsset()
        {
            string name = Path.GetFileNameWithoutExtension(PlyPath);
            string path = $"{k_GaussianAssetDir}/{name}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(path);
            if (asset == null)
                throw new FileNotFoundException(
                    $"[trogir] No splat asset at {path} - run Import Splat Asset first.", path);
            return asset;
        }

        static GameObject LoadPrefab(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                throw new FileNotFoundException($"[trogir] missing prefab {path}", path);
            return prefab;
        }

        static void SetField(object target, string name, object value)
        {
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null)
                throw new MissingFieldException(target.GetType().FullName, name);
            f.SetValue(target, value);
        }

        static object GetField(object target, string name)
        {
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return f?.GetValue(target);
        }
    }
}
