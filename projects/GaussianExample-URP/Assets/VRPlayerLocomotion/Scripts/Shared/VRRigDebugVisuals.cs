using UnityEngine;

namespace VRPlayer
{
    public class VRRigDebugVisuals : MonoBehaviour
    {
        [Header("Tracked Objects")]
        public Transform hmd;
        public Transform playspace;
        public Transform leftController;
        public Transform rightController;

        [Header("Activity Source")]
        public VRPlayer.VRControllerActivityState controllerActivity;

        [Header("Circle Sizes")]
        public float playspaceRadius = 0.25f;
        public float trackedRadius = 0.06f;

        [Header("Line Settings")]
        [Range(0.001f, 0.02f)] public float lineWidth = 0.006f;
        [Range(8, 64)] public int circleSegments = 24;
        public float groundOffset = 0.01f;

        [Header("Colors")]
        public Color hmdColor = Color.cyan;
        public Color playspaceColor = Color.yellow;

        public Color leftIdleColor = Color.white;
        public Color rightIdleColor = Color.white;
        public Color activeControllerColor = Color.blue;

        [Header("Activity Coloring")]
        [Range(0f, 1f)] public float activeThreshold = 0.5f;
        public bool useSmoothColorBlend = false;

        private Material lineMaterial;

        private LineRenderer playspaceCircle;
        private LineRenderer hmdCircle;
        private LineRenderer hmdStem;
        private LineRenderer leftCircle;
        private LineRenderer leftStem;
        private LineRenderer rightCircle;
        private LineRenderer rightStem;

        private void Awake()
        {
            lineMaterial = CreateDefaultLineMaterial();

            playspaceCircle = CreateLineRendererChild("Playspace_Circle", playspaceColor, true);
            hmdCircle = CreateLineRendererChild("HMD_Circle", hmdColor, true);
            hmdStem = CreateLineRendererChild("HMD_Stem", hmdColor, false);
            leftCircle = CreateLineRendererChild("LeftController_Circle", leftIdleColor, true);
            leftStem = CreateLineRendererChild("LeftController_Stem", leftIdleColor, false);
            rightCircle = CreateLineRendererChild("RightController_Circle", rightIdleColor, true);
            rightStem = CreateLineRendererChild("RightController_Stem", rightIdleColor, false);
        }

        private void LateUpdate()
        {
            UpdateStaticColors();
            UpdateControllerColors();

            if (playspace != null)
            {
                Vector3 center = GroundPoint(playspace.position);
                DrawCircle(playspaceCircle, center, playspaceRadius);
            }
            else
            {
                playspaceCircle.enabled = false;
            }

            DrawTrackedVisual(hmd, hmdCircle, hmdStem, trackedRadius);
            DrawTrackedVisual(leftController, leftCircle, leftStem, trackedRadius);
            DrawTrackedVisual(rightController, rightCircle, rightStem, trackedRadius);
        }

        private void UpdateStaticColors()
        {
            SetLineColor(playspaceCircle, playspaceColor);
            SetLineColor(hmdCircle, hmdColor);
            SetLineColor(hmdStem, hmdColor);
        }

        private void UpdateControllerColors()
        {
            float leftWeight = controllerActivity != null ? controllerActivity.LeftWeight : 0f;
            float rightWeight = controllerActivity != null ? controllerActivity.RightWeight : 0f;

            Color leftColor = GetControllerColor(leftIdleColor, leftWeight);
            Color rightColor = GetControllerColor(rightIdleColor, rightWeight);

            SetLineColor(leftCircle, leftColor);
            SetLineColor(leftStem, leftColor);

            SetLineColor(rightCircle, rightColor);
            SetLineColor(rightStem, rightColor);
        }

        private Color GetControllerColor(Color idleColor, float weight)
        {
            if (useSmoothColorBlend)
            {
                return Color.Lerp(idleColor, activeControllerColor, Mathf.Clamp01(weight));
            }

            return weight >= activeThreshold ? activeControllerColor : idleColor;
        }

        private void SetLineColor(LineRenderer lr, Color color)
        {
            if (lr == null)
                return;

            lr.startColor = color;
            lr.endColor = color;
        }

        private void DrawTrackedVisual(Transform target, LineRenderer circle, LineRenderer stem, float radius)
        {
            bool valid = target != null;

            circle.enabled = valid;
            stem.enabled = valid;

            if (!valid)
                return;

            Vector3 ground = GroundPoint(target.position);

            DrawCircle(circle, ground, radius);

            stem.positionCount = 2;
            stem.SetPosition(0, ground);
            stem.SetPosition(1, target.position);
        }

        private Vector3 GroundPoint(Vector3 worldPos)
        {
            float y = playspace != null ? playspace.position.y : transform.position.y;
            return new Vector3(worldPos.x, y + groundOffset, worldPos.z);
        }

        private void DrawCircle(LineRenderer lr, Vector3 center, float radius)
        {
            if (lr == null)
                return;

            lr.enabled = true;
            lr.positionCount = circleSegments + 1;

            float step = Mathf.PI * 2f / circleSegments;

            for (int i = 0; i <= circleSegments; i++)
            {
                float angle = i * step;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                lr.SetPosition(i, center + new Vector3(x, 0f, z));
            }
        }

        private LineRenderer CreateLineRendererChild(string childName, Color color, bool loopStyle)
        {
            GameObject go = new GameObject(childName);
            go.transform.SetParent(transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = false; // circle closes by repeating first point
            lr.alignment = LineAlignment.View;
            lr.textureMode = LineTextureMode.Stretch;
            lr.numCapVertices = loopStyle ? 0 : 2;
            lr.numCornerVertices = 2;

            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;

            lr.material = lineMaterial;
            lr.startColor = color;
            lr.endColor = color;

            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            lr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            return lr;
        }

        private Material CreateDefaultLineMaterial()
        {
            Material builtin = Resources.GetBuiltinResource<Material>("Default-Line.mat");
            if (builtin != null)
                return builtin;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
                return new Material(shader);

            shader = Shader.Find("Unlit/Color");
            if (shader != null)
                return new Material(shader);

            return new Material(Shader.Find("Standard"));
        }
    }

}

