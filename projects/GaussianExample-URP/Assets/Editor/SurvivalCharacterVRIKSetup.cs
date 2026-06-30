using RootMotion.FinalIK;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class SurvivalCharacterVRIKSetup
{
    internal const string PlayerRootName = "VR FinalIK Player";
    internal const string CharacterName = "survival_character";

    [MenuItem("Tools/VR/Update survival_character VRIK References")]
    public static void ApplyFromMenu()
    {
        if (Apply())
            Debug.Log("[SurvivalCharacterVRIKSetup] VRIK references updated on survival_character.");
        else
            Debug.LogError("[SurvivalCharacterVRIKSetup] Failed to update VRIK references.");
    }

    public static bool Apply()
    {
        var character = GameObject.Find(CharacterName);
        if (character == null)
        {
            Debug.LogError($"[SurvivalCharacterVRIKSetup] '{CharacterName}' not found in the open scene.");
            return false;
        }

        var vrik = character.GetComponent<VRIK>();
        if (vrik == null)
        {
            Debug.LogError($"[SurvivalCharacterVRIKSetup] VRIK missing on '{CharacterName}'.");
            return false;
        }

        var animator = character.GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
        {
            Debug.LogError($"[SurvivalCharacterVRIKSetup] Humanoid Animator required on '{CharacterName}'.");
            return false;
        }

        var playerRoot = GameObject.Find(PlayerRootName);
        if (playerRoot == null)
        {
            Debug.LogError($"[SurvivalCharacterVRIKSetup] '{PlayerRootName}' not found in the open scene.");
            return false;
        }

        var vrController = playerRoot.transform.Find("VR Controller");
        if (vrController == null)
        {
            Debug.LogError($"[SurvivalCharacterVRIKSetup] 'VR Controller' not found under '{PlayerRootName}'.");
            return false;
        }

        AssignBoneReferences(vrik, animator);
        AssignIkTargets(vrik, vrController);

        EditorUtility.SetDirty(character);
        if (!Application.isPlaying)
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(character.scene);

        return true;
    }

    static void AssignBoneReferences(VRIK vrik, Animator animator)
    {
        var refs = vrik.references;
        refs.root = animator.transform;

        refs.pelvis = animator.GetBoneTransform(HumanBodyBones.Hips);
        refs.spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        refs.chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        refs.neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        refs.head = animator.GetBoneTransform(HumanBodyBones.Head);

        refs.leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        refs.leftForearm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        refs.leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        refs.leftShoulder = refs.leftUpperArm != null ? refs.leftUpperArm.parent : null;

        refs.rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        refs.rightForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        refs.rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        refs.rightShoulder = refs.rightUpperArm != null ? refs.rightUpperArm.parent : null;

        refs.leftThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        refs.leftCalf = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        refs.leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        refs.leftToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);

        refs.rightThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        refs.rightCalf = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        refs.rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        refs.rightToes = animator.GetBoneTransform(HumanBodyBones.RightToes);

        vrik.references = refs;
    }

    static void AssignIkTargets(VRIK vrik, Transform vrController)
    {
        vrik.solver.spine.headTarget = FindChildByName(vrController, "Head Target");
        vrik.solver.leftArm.target = FindChildByName(vrController, "Left Hand Target");
        vrik.solver.rightArm.target = FindChildByName(vrController, "Right Hand Target");
    }

    static Transform FindChildByName(Transform root, string childName)
    {
        foreach (var transform in root.GetComponentsInChildren<Transform>(true))
        {
            if (transform.name == childName)
                return transform;
        }

        return null;
    }
}

[InitializeOnLoad]
static class SurvivalCharacterVRIKSetupAutoApply
{
    const string VersionKey = "SurvivalCharacterVRIKSetup.Version";
    const int CurrentVersion = 1;

    static SurvivalCharacterVRIKSetupAutoApply()
    {
        EditorApplication.delayCall += TryApplyOnce;
    }

    static void TryApplyOnce()
    {
        EditorApplication.delayCall -= TryApplyOnce;

        if (Application.isPlaying)
            return;

        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded || scene.name != "GSTestScene")
            return;

        var character = GameObject.Find(SurvivalCharacterVRIKSetup.CharacterName);
        var playerRoot = GameObject.Find(SurvivalCharacterVRIKSetup.PlayerRootName);
        if (character == null || playerRoot == null)
            return;

        var vrik = character.GetComponent<VRIK>();
        var vrController = playerRoot.transform.Find("VR Controller");
        if (vrik == null || vrController == null)
            return;

        bool versionPending = SessionState.GetInt(VersionKey, 0) < CurrentVersion;
        if (!versionPending && !NeedsApply(vrik, vrController))
            return;

        if (!SurvivalCharacterVRIKSetup.Apply())
            return;

        SessionState.SetInt(VersionKey, CurrentVersion);
        Debug.Log("[SurvivalCharacterVRIKSetup] Applied VRIK references on survival_character.");
    }

    static bool NeedsApply(VRIK vrik, Transform vrController)
    {
        if (vrik.references.pelvis == null || vrik.references.head == null)
            return true;

        var headTarget = vrik.solver.spine.headTarget;
        if (headTarget == null || !headTarget.IsChildOf(vrController))
            return true;

        var leftTarget = vrik.solver.leftArm.target;
        if (leftTarget == null || !leftTarget.IsChildOf(vrController))
            return true;

        var rightTarget = vrik.solver.rightArm.target;
        if (rightTarget == null || !rightTarget.IsChildOf(vrController))
            return true;

        return false;
    }
}
