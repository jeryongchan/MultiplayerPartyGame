using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FriendSlop.Player
{
    // throwaway debug tool. lets the local player claim a role with a keypress so we can test the
    // RoleRegistry -> spawn flow before any lobby UI exists. press 1 = Sniper, 2 = Criminal.
    // routes through the exact same SetRoleRpc the real lobby will use, delete this once the lobby's in.
    // put it on the Player prefab (owner-gated so each client picks only its own role).
    public class RolePickerDebug : NetworkBehaviour
    {
        private void Update()
        {
            if (!IsOwner || Keyboard.current == null || RoleRegistry.Instance == null)
                return;

            if (Keyboard.current.digit1Key.wasPressedThisFrame)
            {
                RoleRegistry.Instance.SetRoleRpc(PlayerRole.Sniper);
                Debug.Log("[RolePickerDebug] Requested Sniper");
            }
            else if (Keyboard.current.digit2Key.wasPressedThisFrame)
            {
                RoleRegistry.Instance.SetRoleRpc(PlayerRole.Criminal);
                Debug.Log("[RolePickerDebug] Requested Criminal");
            }
        }
    }
}
