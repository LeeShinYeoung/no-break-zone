using PugMod;
using UnityEngine;

// 기획서 §4 wants the remote itself to react, not just the pylon: "벽에 갇힌 파일런은 파일런 쪽
// 이펙트가 잘 보이지 않으므로, 작동 여부를 손에 든 물건이 알려줘야 한다."
//
// A sound only. §4 also describes the remote's screen flashing, which needs the held item's
// renderer and is not done.
//
// This deliberately does NOT know whether the toggle succeeded. It fires on pressing the button
// with the remote in hand, so it answers "the remote went off" rather than "a pylon switched" —
// which is the question a player standing outside their own walls is actually asking. The pylon's
// own effect and sound, already there from 5단계, answer the other one whenever it is visible.
//
// CLIENT ONLY, driven from NoBreakZoneMod.Update. The toggle itself is server-side in
// NoBreakZoneRemoteSystem; this is just the click.
public static class NoBreakZoneRemoteFeedback
{
    // A small mechanical click for a handheld device. Named rather than numbered, for the reason
    // ObjectID taught: names resolve against the real game assembly at build time.
    private const SfxID UseSound = SfxID.itemSwitch;

    private static ObjectID _remoteObjectID = ObjectID.None;

    public static void Update()
    {
        var manager = Manager.main;
        if (manager == null || manager.player == null)
        {
            return;
        }

        // No feedback while a menu is up — the click there belongs to the UI, not to the remote.
        if (Manager.ui != null && (Manager.ui.isShowingMap || Manager.ui.isAnyInventoryShowing))
        {
            return;
        }

        if (!manager.player.clientInput.IsButtonStateSet(
                CommandInputButtonStateNames.SecondInteract_Pressed))
        {
            return;
        }

        if (!IsHoldingRemote(manager.player))
        {
            return;
        }

        API.Audio.PlaySfx((int)UseSound, manager.player.transform.position);
    }

    private static bool IsHoldingRemote(PlayerController player)
    {
        if (_remoteObjectID == ObjectID.None)
        {
            _remoteObjectID = API.Authoring.GetObjectID(NoBreakZoneObjectNames.Remote);
            if (_remoteObjectID == ObjectID.None)
            {
                return false;
            }
        }

        return player.visuallyEquippedContainedObject.objectData.objectID == _remoteObjectID;
    }
}
