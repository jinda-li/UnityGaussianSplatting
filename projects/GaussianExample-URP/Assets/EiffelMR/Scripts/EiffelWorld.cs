using UnityEngine;

namespace EiffelMR
{
    // The thing the miniature is a miniature OF.
    //
    // The interaction - bubble, poke, throw, arrive - does not care what the
    // tower is made of. It needs to scale it, make it look like a cloud or a
    // solid, grow it into the world on landing and shrink it back. Two worlds
    // implement that:
    //
    //   SplatDiveWorld  the trained Gaussian splat, driven through the Garden's
    //                   TabletopDiveController (the MR build)
    //   MeshWorld       the Blender scene exported as meshes, with the tower
    //                   drawn as a point cloud sampled off its own surface (runs
    //                   on a desktop, no headset and no trained splat needed)
    //
    // Both keep the rule the design rests on: there is one tower, and the one
    // in the player's hand is the one they arrive in.
    public abstract class EiffelWorld : MonoBehaviour
    {
        /// World scale of the miniature, e.g. 0.0009 for 29 cm of a 324 m tower.
        public abstract float TableScale { get; }

        /// A transition the session has to wait out.
        public abstract bool IsBusy { get; }

        /// The miniature is a miniature: it can be held and thrown.
        public abstract bool InPlace { get; }

        /// The world has been arrived in.
        public abstract bool IsImmersive { get; }

        /// Whether there is anything to scale. False for a splat world with no
        /// splat assigned yet; the flow probe skips the growth checks then.
        public abstract bool CanGrow { get; }

        /// The world decides where the miniature appears (the splat rig is
        /// placed by its dive controller). When false the session places the
        /// miniature itself.
        public virtual bool PlacesItself => false;

        /// Current scale of the world, for tests.
        public abstract float MeasuredScale { get; }

        public virtual void Present() { }

        public abstract void SetMiniatureScale(float magnitude);

        /// 0 = drifting cloud of points, 1 = solid tower.
        public abstract void SetSolidify(float amount);

        public virtual void BeginFlight() { }
        public virtual void EndFlight() { }

        /// A throw was caught or cancelled mid-air: back to a miniature.
        public virtual void CancelFlight()
        {
            SetMiniatureScale(TableScale);
            SetSolidify(0f);
            EndFlight();
        }

        /// Grow the world from wherever the miniature landed until the player
        /// is standing in it.
        public abstract void Arrive(Vector3 landing, float duration);

        /// Shrink it back to a miniature. IsBusy until done.
        public abstract void Leave();

        /// Immediate version of Leave, for resets.
        public abstract void ResetToTable();
    }
}
