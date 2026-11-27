using Flagsmith;
using UnityEngine;

/// <summary>
/// The smallest useful setup. Add this next to a FlagsmithBehaviour (environment key set in its Inspector)
/// and press Play. Change "move_speed" or "welcome_text" in Flagsmith and watch the Console.
/// </summary>
[RequireComponent(typeof(FlagsmithBehaviour))]
public class FlagsmithQuickstart : MonoBehaviour
{
    // Remote config: these follow the flags of the same name, and keep these defaults when offline.
    [FlagsmithValue("move_speed")] public float moveSpeed = 5f;
    [FlagsmithValue("welcome_text")] public string welcomeText = "Hello";
    [FlagsmithValue("new_shop")] public bool newShop;   // bools follow the flag's on/off state

    FlagsmithBehaviour flagsmith;

    void Awake()
    {
        flagsmith = GetComponent<FlagsmithBehaviour>();
        flagsmith.Bind(this);
        flagsmith.FlagsChanged += _ => Debug.Log($"Flags from {flagsmith.Flags.Source}: speed {moveSpeed}, text \"{welcomeText}\", new shop {newShop}");
    }

    // Experimentation: call when the player actually sees the thing under test,
    // then report the outcome you want to improve.
    public void OnShopOpened() => flagsmith.GetExperimentFlag("shop_layout");
    public void OnPurchase(float price) => flagsmith.TrackEvent("purchase", price);
}
