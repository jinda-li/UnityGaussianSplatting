using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace EiffelMR.EditorTools
{
    // Builds Eiffel_Desktop: the whole bubble -> poke -> throw -> arrive flow on
    // a desktop, with the Blender scene exported as meshes standing in for the
    // trained splat.
    //
    // Input is whatever tools/eiffel/export_unity.py wrote to
    // Assets/EiffelMR/Generated (not committed - see the .gitignore note there).
    // Re-run the export, then this, whenever the Blender scene changes.
    public static class EiffelDesktopSceneBuilder
    {
        const string k_Gen = "Assets/EiffelMR/Generated";
        const string k_Model = k_Gen + "/Model";
        const string k_Tex = k_Gen + "/Textures";
        const string k_Mat = k_Gen + "/Materials";
        const string k_ScenePath = "Assets/EiffelMR/Eiffel_Desktop.unity";

        // 0.0009 x 324 m = 29 cm in the hand.
        const float k_TableScale = 0.0009f;
        const int k_PointCount = 220000;

        [MenuItem("Tools/Eiffel MR/Build Eiffel Desktop Scene")]
        public static void Build()
        {
            if (!File.Exists(k_Model + "/Tower.fbx") || !File.Exists(k_Model + "/Environment.fbx"))
            {
                Debug.LogError("[EiffelDesktop] No export in " + k_Model + ". Run " +
                               "tools/eiffel/export_unity.py first.");
                return;
            }
            ConfigureImports();
            var mats = BuildMaterials();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ---- light, camera, room ---------------------------------------
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(45f, 160f, 0f);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 6000f;
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            camGo.AddComponent<AudioListener>();
            // Tilted down: the bubble appears at chest height half a metre
            // away, which is below a level gaze's field of view on a monitor.
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f),
                                                   Quaternion.Euler(24f, 0f, 0f));
            var rig = camGo.AddComponent<DesktopEiffelRig>();

            var room = new GameObject("Room (stands in for passthrough)");
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(room.transform, false);
            floor.transform.localScale = new Vector3(14f, 0.1f, 14f);
            floor.transform.localPosition = new Vector3(0f, -0.05f, 0f);
            floor.GetComponent<MeshRenderer>().sharedMaterial = mats["_RoomFloor"];

            // ---- play group -------------------------------------------------
            var playRoot = new GameObject("EiffelMR Play");
            var playGroup = new GameObject("Play Group");
            playGroup.transform.SetParent(playRoot.transform, false);

            var bubble = BuildBubble(playGroup.transform);
            var ring = EiffelMRSceneBuilder.BuildRing(playGroup.transform);
            var reveal = EiffelMRSceneBuilder.BuildSkyShell(playRoot.transform);
            var miniature = BuildMiniatureBody(playGroup.transform);
            var tower = miniature.GetComponent<ThrownTower>();

            // ---- the world, under the miniature ------------------------------
            var worldRoot = new GameObject("EiffelWorld").transform;
            worldRoot.SetParent(miniature.transform, false);
            worldRoot.localScale = Vector3.one * k_TableScale;

            var towerGo = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(k_Model + "/Tower.fbx"));
            towerGo.name = "Tower";
            towerGo.transform.SetParent(worldRoot, false);
            var towerRenderers = towerGo.GetComponentsInChildren<MeshRenderer>();
            foreach (var r in towerRenderers)
                AssignByName(r, mats);

            var envGo = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(k_Model + "/Environment.fbx"));
            envGo.name = "Environment";
            envGo.transform.SetParent(worldRoot, false);
            PrefabUtility.UnpackPrefabInstance(envGo, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            var markers = PopulateEnvironment(envGo.transform, mats);

            var points = BuildPoints(towerGo, towerRenderers, markers, worldRoot, mats);

            // ---- controller -------------------------------------------------
            var worldCtl = new GameObject("EiffelWorld Controller").AddComponent<MeshWorld>();
            worldCtl.m_WorldRoot = worldRoot;
            worldCtl.m_Miniature = miniature.transform;
            worldCtl.m_Environment = envGo;
            worldCtl.m_Points = points;
            worldCtl.m_TowerRenderers = towerRenderers;
            worldCtl.m_Hero = markers.hero;
            worldCtl.m_HeroLook = markers.heroLook;
            worldCtl.m_SunMarker = markers.sun;
            worldCtl.m_Head = camGo.transform;
            worldCtl.m_Camera = cam;
            worldCtl.m_Sun = sun;
            worldCtl.m_Room = room;
            worldCtl.m_TableScale = k_TableScale;
            worldCtl.m_Skybox = mats["_Skybox"];
            RenderSettings.skybox = mats["_Skybox"];
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.sun = sun;
            envGo.SetActive(false);

            // ---- session ----------------------------------------------------
            var session = playRoot.AddComponent<EiffelBubbleSession>();
            session.m_PlayGroup = playGroup;
            session.m_Bubble = bubble;
            session.m_Tower = tower;
            session.m_Ring = ring;
            session.m_Reveal = reveal;
            session.m_World = worldCtl;
            session.m_Head = camGo.transform;
            session.m_FloorY = 0f;
            // The headset button. On a desktop, DesktopEiffelRig maps Space to
            // the same Toggle().
            session.m_ToggleAction = new InputAction("EiffelToggle", InputActionType.Button);
            session.m_ToggleAction.AddBinding("<XRController>{RightHand}/primaryButton");

            bubble.m_Tower = miniature.GetComponent<Rigidbody>();
            tower.m_Ring = ring;
            tower.m_Reveal = reveal;
            tower.m_World = worldCtl;
            rig.m_Session = session;
            rig.m_Camera = cam;

            var revealRenderer = reveal.GetComponent<MeshRenderer>();
            revealRenderer.sharedMaterial = mats["_HexSky"];

            var probe = new GameObject("FlowProbe").AddComponent<EiffelFlowProbe>();
            probe.m_RunOnStart = false;

            Directory.CreateDirectory(Path.GetDirectoryName(k_ScenePath));
            EditorSceneManager.SaveScene(scene, k_ScenePath);
            Debug.Log("[EiffelDesktop] Built " + k_ScenePath + " - Play, then Space for " +
                      "a bubble. Tick FlowProbe.m_RunOnStart for the automated run.");
        }

        // --------------------------------------------------------------------
        // Imports

        static void ConfigureImports()
        {
            foreach (var path in Directory.GetFiles(k_Model, "*.fbx"))
            {
                var p = path.Replace('\\', '/');
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                if (!mi)
                    continue;
                bool isTower = p.EndsWith("/Tower.fbx");
                bool dirty = false;
                if (mi.bakeAxisConversion != true) { mi.bakeAxisConversion = true; dirty = true; }
                if (mi.useFileScale != true) { mi.useFileScale = true; dirty = true; }
                if (mi.globalScale != 1f) { mi.globalScale = 1f; dirty = true; }
                // Read/Write only where the points are sampled from.
                if (mi.isReadable != isTower) { mi.isReadable = isTower; dirty = true; }
                if (mi.importCameras) { mi.importCameras = false; dirty = true; }
                if (mi.importLights) { mi.importLights = false; dirty = true; }
                if (mi.importAnimation) { mi.importAnimation = false; dirty = true; }
                if (mi.animationType != ModelImporterAnimationType.None)
                { mi.animationType = ModelImporterAnimationType.None; dirty = true; }
                if (mi.meshCompression != ModelImporterMeshCompression.Off)
                { mi.meshCompression = ModelImporterMeshCompression.Off; dirty = true; }
                if (mi.indexFormat != ModelImporterIndexFormat.UInt32)
                { mi.indexFormat = ModelImporterIndexFormat.UInt32; dirty = true; }
                if (dirty)
                    mi.SaveAndReimport();
            }

            foreach (var name in new[] { "lawn_nor.jpg", "gravel_nor.jpg", "iron_nor.jpg" })
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(k_Tex + "/" + name);
                if (ti && ti.textureType != TextureImporterType.NormalMap)
                {
                    ti.textureType = TextureImporterType.NormalMap;
                    ti.SaveAndReimport();
                }
            }
            var sky = (TextureImporter)AssetImporter.GetAtPath(k_Tex + "/sky.hdr");
            if (sky && (sky.textureShape != TextureImporterShape.Texture2D ||
                        sky.maxTextureSize != 4096 || sky.mipmapEnabled))
            {
                // A 2D lat-long, not a cubemap: Skybox/Panoramic and the hex
                // shell both sample it the same way, rotation included.
                sky.textureShape = TextureImporterShape.Texture2D;
                sky.maxTextureSize = 4096;
                sky.mipmapEnabled = false;
                sky.wrapModeU = TextureWrapMode.Repeat;
                sky.wrapModeV = TextureWrapMode.Clamp;
                sky.SaveAndReimport();
            }
            var leaves = (TextureImporter)AssetImporter.GetAtPath(k_Tex + "/leaves_rgba.png");
            if (leaves && !leaves.alphaIsTransparency)
            {
                leaves.alphaIsTransparency = true;
                leaves.SaveAndReimport();
            }
        }

        // --------------------------------------------------------------------
        // Materials

        static Dictionary<string, Material> BuildMaterials()
        {
            Directory.CreateDirectory(k_Mat);
            var m = new Dictionary<string, Material>();
            Texture2D T(string n) => AssetDatabase.LoadAssetAtPath<Texture2D>(k_Tex + "/" + n);

            // UVs on the static meshes are world metres (export_unity.metre_uvs),
            // so tiling = 1 / tile size in metres.
            m["lawn"] = Lit("Lawn", new Color(0.86f, 0.95f, 0.78f), T("lawn_diff.jpg"), 1f / 3.5f, T("lawn_nor.jpg"));
            m["gravel"] = Lit("Gravel", new Color(1.12f, 1.09f, 1.0f), T("gravel_diff.jpg"), 1f / 2.2f, T("gravel_nor.jpg"));
            m["stone"] = Lit("Stone", new Color(0.62f, 0.57f, 0.48f), T("stone_diff.jpg"), 1f / 3f, null);
            m["haussmann"] = Lit("Haussmann", new Color(0.80f, 0.74f, 0.63f));
            m["zinc"] = Lit("Zinc", new Color(0.36f, 0.40f, 0.45f));
            m["pierre"] = Lit("PalePierre", new Color(0.84f, 0.80f, 0.71f));
            m["kiosk"] = Lit("Kiosk", new Color(0.20f, 0.26f, 0.24f));
            m["green"] = Lit("ParkGreen", new Color(0.10f, 0.20f, 0.14f));
            m["steel"] = Lit("Steel", new Color(0.18f, 0.18f, 0.19f));
            m["default"] = Lit("Default", new Color(0.55f, 0.55f, 0.52f));
            m["bark"] = Lit("Bark", new Color(0.95f, 0.95f, 0.92f), T("bark_diff.png"), 1f, null);
            m["branches"] = Lit("Branches", Color.white, T("branches_diff.png"), 1f, null);
            m["leaves"] = Lit("Leaves", new Color(0.72f, 0.92f, 0.70f), T("leaves_rgba.png"), 1f, null,
                              cutout: true);
            m["lamp"] = Lit("Lamp", new Color(0.07f, 0.09f, 0.08f));
            m["glass"] = Lit("LampGlass", new Color(0.9f, 0.9f, 0.85f));
            m["bench"] = Lit("Bench", new Color(0.14f, 0.28f, 0.19f));
            m["_RoomFloor"] = Lit("RoomFloor", new Color(0.32f, 0.32f, 0.33f));

            // Measured off Commons photographs (see the plan doc): painted iron
            // hue 24, saturation ~0.3, value ~0.38.
            m["iron"] = Dissolve("TowerIron", new Color(0.385f, 0.315f, 0.265f));
            m["pier"] = Dissolve("TowerPier", new Color(0.59f, 0.55f, 0.47f));
            m["_Points"] = Save(new Material(Shader.Find("EiffelMR/TowerPoints")), "TowerPoints");

            var sky = AssetDatabase.LoadAssetAtPath<Texture2D>(k_Tex + "/sky.hdr");
            float rot = SkyRotation();
            var skybox = new Material(Shader.Find("Skybox/Panoramic"));
            skybox.SetTexture("_MainTex", sky);
            skybox.SetFloat("_Rotation", rot);
            skybox.SetFloat("_Exposure", 1.0f);
            skybox.SetFloat("_Mapping", 1f);           // latitude-longitude
            skybox.SetFloat("_ImageType", 0f);         // 360
            m["_Skybox"] = Save(skybox, "Skybox");

            var hex = new Material(Shader.Find("EiffelMR/HexSkyReveal"));
            hex.SetTexture("_Pano", sky);
            hex.SetFloat("_UsePano", 1f);
            hex.SetFloat("_Rotation", rot);
            m["_HexSky"] = Save(hex, "HexSkyReveal_Desktop");
            return m;
        }

        // Rotation that puts the panorama's sun where the Sun marker says it is.
        // Skybox/Panoramic maps a direction d, rotated by R about Y, to
        // u = 0.5 - atan2(d.z, d.x) / 2pi; Blender's sun_direction reports the
        // same (0.5 - u) * 360 as the image azimuth. So R is the image azimuth
        // minus the Unity-space azimuth of the sun.
        static float SkyRotation()
        {
            string json = k_Model + "/sky.json";
            var env = AssetDatabase.LoadAssetAtPath<GameObject>(k_Model + "/Environment.fbx");
            var sun = env ? env.transform.Find("Sun") : null;
            if (!File.Exists(json) || !sun)
            {
                Debug.LogWarning("[EiffelDesktop] No sky.json or Sun marker; sky unrotated.");
                return 0f;
            }
            var info = JsonUtility.FromJson<SkyInfo>(File.ReadAllText(json));
            Vector3 d = sun.localPosition.normalized;
            float unityAz = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
            float r = Mathf.Repeat(info.image_azimuth_deg - unityAz, 360f);
            Debug.Log($"[EiffelDesktop] sky rotation {r:F1} (image az {info.image_azimuth_deg:F1}, " +
                      $"sun az in Unity {unityAz:F1}, elevation {Mathf.Asin(d.y) * Mathf.Rad2Deg:F1})");
            return r;
        }

        [System.Serializable]
        class SkyInfo { public float image_azimuth_deg; public float elevation_deg; }

        static Material Lit(string name, Color colour, Texture2D map = null, float tiling = 1f,
                            Texture2D normal = null, bool cutout = false)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", colour);
            mat.SetFloat("_Smoothness", 0.12f);
            mat.SetFloat("_Metallic", 0f);
            if (map)
            {
                mat.SetTexture("_BaseMap", map);
                mat.SetTextureScale("_BaseMap", Vector2.one * tiling);
            }
            if (normal)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetTextureScale("_BumpMap", Vector2.one * tiling);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (cutout)
            {
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_Cutoff", 0.45f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", 0f);                // leaf cards, both sides
                mat.renderQueue = (int)RenderQueue.AlphaTest;
            }
            mat.enableInstancing = true;
            return Save(mat, name);
        }

        static Material Dissolve(string name, Color colour)
        {
            var mat = new Material(Shader.Find("EiffelMR/TowerDissolve"));
            mat.SetColor("_BaseColor", colour);
            return Save(mat, name);
        }

        static Material Save(Material mat, string name)
        {
            string path = k_Mat + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing)
            {
                existing.shader = mat.shader;
                existing.CopyPropertiesFromMaterial(mat);
                existing.shaderKeywords = mat.shaderKeywords;
                existing.renderQueue = mat.renderQueue;
                existing.enableInstancing = mat.enableInstancing;
                EditorUtility.SetDirty(existing);
                return existing;
            }
            mat.name = name;
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        // Blender material name -> Unity material.
        static Material Pick(string blenderName, Dictionary<string, Material> m)
        {
            string n = blenderName.ToLowerInvariant();
            if (n.Contains("eiffeliron")) return m["iron"];
            if (n.Contains("eiffelpier")) return m["pier"];
            if (n.Contains("lawn")) return m["lawn"];
            if (n.Contains("sand") || n.Contains("walk") || n.Contains("gravel") || n.Contains("allee"))
                return m["gravel"];
            if (n.Contains("leaves")) return m["leaves"];
            if (n.Contains("branch")) return m["branches"];
            if (n.Contains("trunk")) return m["bark"];
            if (n.Contains("haussmann")) return m["haussmann"];
            if (n.Contains("zinc")) return m["zinc"];
            if (n.Contains("pierre") || n.Contains("chaillot")) return m["pierre"];
            if (n.Contains("kiosk")) return m["kiosk"];
            if (n.Contains("green") || n.Contains("fence")) return m["green"];
            if (n.Contains("steel") || n.Contains("mast")) return m["steel"];
            if (n.Contains("bulb") || n.Contains("glass")) return m["glass"];
            if (n.Contains("lamp")) return m["lamp"];
            if (n.Contains("bench")) return m["bench"];
            if (n.Contains("stone") || n.Contains("pier") || n.Contains("plinth")) return m["stone"];
            return m["default"];
        }

        static void AssignByName(Renderer r, Dictionary<string, Material> m)
        {
            var src = r.sharedMaterials;
            var dst = new Material[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = Pick(src[i] ? src[i].name : "", m);
            r.sharedMaterials = dst;
        }

        // --------------------------------------------------------------------
        // Environment

        struct Markers { public Transform hero, heroLook, sun; }

        static Markers PopulateEnvironment(Transform env, Dictionary<string, Material> m)
        {
            var mk = new Markers
            {
                hero = env.Find("Hero"),
                heroLook = env.Find("HeroLook"),
                sun = env.Find("Sun"),
            };
            foreach (var r in env.GetComponentsInChildren<MeshRenderer>(true))
                AssignByName(r, m);

            // Prototype models, one per kind, with their materials assigned once.
            var protos = new Dictionary<string, GameObject>();
            var counts = new Dictionary<string, int>();
            var empties = new List<Transform>();
            foreach (Transform child in env)
                if (child.name.StartsWith("I__"))
                    empties.Add(child);
            foreach (var e in empties)
            {
                string[] parts = e.name.Split(new[] { "__" }, System.StringSplitOptions.None);
                if (parts.Length < 3)
                    continue;
                string kind = parts[1];
                if (!protos.TryGetValue(kind, out var model))
                {
                    model = AssetDatabase.LoadAssetAtPath<GameObject>(k_Model + "/" + kind + ".fbx");
                    protos[kind] = model;
                }
                if (!model)
                    continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                inst.transform.SetParent(e, false);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one;
                foreach (var r in inst.GetComponentsInChildren<MeshRenderer>())
                {
                    AssignByName(r, m);
                    // Hundreds of trees: shadows from all of them at this
                    // density cost more than they add from standing height.
                    r.shadowCastingMode = kind == "TreeProto" ? ShadowCastingMode.On
                                                              : ShadowCastingMode.Off;
                }
                counts[kind] = counts.TryGetValue(kind, out int c) ? c + 1 : 1;
            }
            var sb = new System.Text.StringBuilder("[EiffelDesktop] instances:");
            foreach (var kv in counts)
                sb.Append($" {kv.Key}={kv.Value}");
            Debug.Log(sb.ToString());
            if (!mk.hero || !mk.heroLook || !mk.sun)
                Debug.LogWarning("[EiffelDesktop] Hero/HeroLook/Sun marker missing from the export.");
            return mk;
        }

        // --------------------------------------------------------------------
        // Points

        static TowerPointCloud BuildPoints(GameObject towerGo, MeshRenderer[] towerRenderers,
                                           Markers mk, Transform worldRoot,
                                           Dictionary<string, Material> m)
        {
            var mf = towerGo.GetComponentInChildren<MeshFilter>();
            var mesh = mf ? mf.sharedMesh : null;
            var go = new GameObject("TowerPoints");
            var pc = go.AddComponent<TowerPointCloud>();
            go.GetComponent<MeshRenderer>().sharedMaterial = m["_Points"];
            if (!mesh || !mesh.isReadable)
            {
                Debug.LogError("[EiffelDesktop] Tower mesh missing or not readable.");
                return pc;
            }
            var renderer = mf.GetComponent<MeshRenderer>();
            var albedo = new Color[mesh.subMeshCount];
            for (int i = 0; i < albedo.Length; i++)
            {
                var mat = renderer && i < renderer.sharedMaterials.Length ? renderer.sharedMaterials[i] : null;
                albedo[i] = mat ? mat.GetColor("_BaseColor") : new Color(0.385f, 0.315f, 0.265f);
            }
            // Sun direction in the tower mesh's own space, so the pre-lit points
            // agree with the light the solid tower is drawn under.
            Vector3 sunWorld = mk.sun ? (mk.sun.position - worldRoot.position).normalized : Vector3.up;
            Vector3 sunLocal = mf.transform.InverseTransformDirection(sunWorld);
            var pts = TowerPointCloud.BuildMesh(mesh, albedo, k_PointCount, sunLocal);
            string path = k_Gen + "/TowerPoints.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(pts, path);
            go.GetComponent<MeshFilter>().sharedMesh = pts;

            // The mesh's own units decide what "3 metres of drift" means. Not
            // mesh.bounds: the FBX node carries a 90 degree axis rotation, so the
            // mesh's local Y is a horizontal extent, and reading it as the height
            // made every drift and noise size 2.4x too small. The world root is
            // in metres, so the node's scale relative to it is the conversion.
            float nodeScale = mf.transform.lossyScale.x / Mathf.Max(worldRoot.lossyScale.x, 1e-9f);
            float unitsPerMetre = 1f / Mathf.Max(nodeScale, 1e-6f);
            pc.m_Drift = 2.5f * unitsPerMetre;
            foreach (var r in towerRenderers)
                foreach (var mat in r.sharedMaterials)
                    if (mat && mat.HasProperty("_CellSize"))
                        mat.SetFloat("_CellSize", 3f * unitsPerMetre);
            // The point shader colours by height and swirls about the tower's
            // axis, so it needs that axis, and the height, in mesh units.
            var pm = m["_Points"];
            pm.SetVector("_UpOS", mf.transform.InverseTransformDirection(worldRoot.up).normalized);
            pm.SetFloat("_Height", 324f * unitsPerMetre);
            pm.SetFloat("_EmberRise", 70f * unitsPerMetre);
            EditorUtility.SetDirty(pm);
            Debug.Log($"[EiffelDesktop] tower mesh {mesh.vertexCount} verts, {unitsPerMetre:F3} " +
                      $"mesh units per metre, {k_PointCount} points");
            // Place the points in the same frame as the mesh they came from.
            go.transform.SetParent(mf.transform, false);
            return pc;
        }

        // --------------------------------------------------------------------
        // Bubble and the object that is held

        static TowerBubble BuildBubble(Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "TowerBubble";
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * 0.40f;
            var shell = go.GetComponent<MeshRenderer>();
            shell.sharedMaterial = EiffelMRSceneBuilder.LoadOrCreate("BubbleGlass", "EiffelMR/BubbleGlass");
            shell.shadowCastingMode = ShadowCastingMode.Off;
            go.GetComponent<SphereCollider>().isTrigger = true;
            var bubble = go.AddComponent<TowerBubble>();
            bubble.m_FollowTower = true;
            bubble.m_CentreOffset = 0.15f;
            return bubble;
        }

        static GameObject BuildMiniatureBody(Transform parent)
        {
            var go = new GameObject("Miniature");
            go.transform.SetParent(parent, false);
            var body = go.AddComponent<Rigidbody>();
            body.mass = 0.4f;
            body.isKinematic = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            // The tower at table scale: a 12 cm footprint, 29 cm tall, standing
            // on its own origin.
            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 0.145f, 0f);
            col.size = new Vector3(0.11f, 0.29f, 0.11f);
            var grab = go.AddComponent<XRGrabInteractable>();
            grab.useDynamicAttach = true;
            grab.throwOnDetach = false;
            go.AddComponent<ThrownTower>();
            return go;
        }
    }
}
