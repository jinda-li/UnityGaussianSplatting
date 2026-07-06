using UnityEngine;

[CreateAssetMenu(fileName = "New Info Orb Data", menuName = "VR Interaction/Info Orb Data")]
public class InfoOrbData : ScriptableObject
{
    [SerializeField] private string title;
    [SerializeField, TextArea(3, 10)] private string description;

    public string Title => title;
    public string Description => description;
}
