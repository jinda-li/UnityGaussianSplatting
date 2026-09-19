using UnityEngine;
using GardenMR;

namespace EiffelMR
{
    // The trained splat, grown by the Garden's TabletopDiveController.
    //
    // Everything here used to live inside ThrownTower and EiffelBubbleSession;
    // it moved behind EiffelWorld so that the same interaction can also run on
    // a mesh world with no splat at all.
    public class SplatDiveWorld : EiffelWorld
    {
        public TabletopDiveController m_Dive;

        [Tooltip("Scales the splat without moving the rig. Left empty, the splat " +
                 "renderer's own transform is scaled instead.")]
        public SplatHandleRig m_HandleRig;

        [Tooltip("Only used when there is no handle rig.")]
        public Transform m_SplatRoot;

        [Tooltip("Draws the miniature as a drifting cloud of its own splats.")]
        public TowerParticles m_Particles;

        [Tooltip("Spawn point whose splat-local position becomes where the player " +
                 "stands. For the Eiffel scene: the hero viewpoint, splat-local " +
                 "(34, -116, 1.65).")]
        public SplatSpawnPoint m_LandingSpawn;

        void Awake()
        {
            if (!m_Dive)
                m_Dive = FindFirstObjectByType<TabletopDiveController>();
            if (m_Dive)
            {
                if (!m_HandleRig) m_HandleRig = m_Dive.m_HandleRig;
                if (!m_SplatRoot && m_Dive.m_SplatRenderer)
                    m_SplatRoot = m_Dive.m_SplatRenderer.transform;
                if (!m_LandingSpawn) m_LandingSpawn = m_Dive.m_DefaultSpawnPoint;
            }
            if (!m_Particles)
                m_Particles = FindFirstObjectByType<TowerParticles>();
        }

        public override float TableScale => m_Dive ? m_Dive.m_DefaultTableScale : 0.0009f;
        public override bool IsBusy => m_Dive && m_Dive.IsBusy;
        public override bool InPlace =>
            !m_Dive || m_Dive.CurrentState == TabletopDiveController.State.Place;
        public override bool IsImmersive =>
            m_Dive && m_Dive.CurrentState == TabletopDiveController.State.Immersive;
        public override bool CanGrow => m_Dive && m_Dive.m_SplatRenderer;
        public override bool PlacesItself => true;
        public override float MeasuredScale =>
            m_SplatRoot ? m_SplatRoot.localScale.x : 0f;

        public override void Present()
        {
            if (m_Dive)
                m_Dive.RequestSummon();
        }

        public override void SetMiniatureScale(float magnitude)
        {
            if (m_HandleRig)
                m_HandleRig.ApplyScaleKeepRig(magnitude);
            else if (m_SplatRoot)
                m_SplatRoot.localScale = Vector3.one * magnitude;
        }

        public override void SetSolidify(float amount)
        {
            if (m_Particles)
                m_Particles.Solidify = amount;
        }

        public override void BeginFlight()
        {
            if (!m_Dive)
                return;
            m_Dive.SetRigGrabEnabled(false);
            // Still in Place while it is in the air, so without this an
            // environment switch could fire mid-flight.
            m_Dive.BeginExclusiveTransition();
        }

        public override void EndFlight()
        {
            if (m_Dive)
                m_Dive.EndExclusiveTransition();
        }

        public override void Arrive(Vector3 landing, float duration)
        {
            if (!m_Dive)
                return;
            if (duration > 0f)
                m_Dive.m_DiveDuration = duration;
            // Dive() reads the splat's current scale as its starting magnitude,
            // so the growth from the arc carries straight on into the growth of
            // the world.
            m_Dive.Dive(m_LandingSpawn);
        }

        public override void Leave()
        {
            if (m_Dive)
                m_Dive.Return();
        }

        public override void ResetToTable()
        {
            SetMiniatureScale(TableScale);
            SetSolidify(0f);
        }
    }
}
