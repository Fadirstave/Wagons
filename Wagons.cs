using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Wagons", "FadirStave", "0.1.0")]
    [Description("Build, tow, and manage horse-drawn wagons with protected storage.")]
    public class Wagons : RustPlugin
    {
        private const string FramePrefab = "assets/content/vehicles/modularcar/car_chassis_2module.entity.prefab";
        private const string LargeBoxPrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";

        private static readonly Vector3 FrameSpawnOffset = new Vector3(0f, 0f, -2f);
        private static readonly Vector3 LeftBoxOffset = new Vector3(-0.55f, 0.55f, -0.25f);
        private static readonly Vector3 RightBoxOffset = new Vector3(0.55f, 0.55f, -0.25f);
        private static readonly Vector3 TowedOffset = new Vector3(0f, 0.25f, -3.2f);

        private const int RequiredHqm = 100;
        private const int RequiredLargeBoxes = 2;
        private const int RequiredRope = 10;

        private const float RemoveDistance = 6f;
        private const float AttachDistance = 6f;
        private const float UseCooldown = 0.75f;
        private const float TurnSpeed = 4f;
        private const float FollowSpeed = 8f;

        private ItemDefinition _hqmDef;
        private ItemDefinition _largeBoxDef;
        private ItemDefinition _ropeDef;

        private readonly Dictionary<BaseEntity, WagonData> _wagons = new Dictionary<BaseEntity, WagonData>();
        private readonly Dictionary<ulong, WagonData> _wagonsByOwner = new Dictionary<ulong, WagonData>();
        private readonly Dictionary<ulong, WagonData> _wagonsByEntityId = new Dictionary<ulong, WagonData>();

        private class WagonData
        {
            public ulong OwnerID;
            public BaseEntity wagonFrame;
            public BaseEntity storageBox1;
            public BaseEntity storageBox2;
            public BaseEntity horse;
            public bool isTowed;
            public float LastUseTime;
        }

        private void Init()
        {
            _hqmDef = ItemManager.FindItemDefinition("metal.refined");
            _largeBoxDef = ItemManager.FindItemDefinition("box.wooden.large");
            _ropeDef = ItemManager.FindItemDefinition("rope");
        }

        [ChatCommand("wagon")]
        private void CmdWagon(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            if (args != null && args.Length > 0 && args[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                CmdWagonRemove(player);
                return;
            }

            if (_hqmDef == null || _largeBoxDef == null || _ropeDef == null)
            {
                SendReply(player, "Item definitions failed to load. Contact an admin.");
                return;
            }

            if (_wagonsByOwner.ContainsKey(player.userID))
            {
                SendReply(player, "You already have a wagon.");
                return;
            }

            var missing = GetMissingRequirements(player.inventory);
            if (missing.Count > 0)
            {
                SendReply(player, $"Missing materials: {string.Join(", ", missing)}.");
                return;
            }

            ConsumeRequirements(player.inventory);

            if (!TrySpawnWagon(player, out var wagon))
            {
                RefundRequirements(player.inventory);
                SendReply(player, "Failed to build wagon, try a more open location.");
                return;
            }

            TrackWagon(wagon);
            SendReply(player, "Wagon built. Mount a horse, look backward, and press USE (E) to attach/detach.");
        }

        [ChatCommand("removewagons")]
        private void CmdRemoveWagons(BasePlayer player, string command, string[] args)
        {
            if (player != null && !player.IsAdmin)
            {
                SendReply(player, "You do not have permission to use this command.");
                return;
            }

            RemoveAllWagons();
            if (player != null)
            {
                SendReply(player, "All wagons removed.");
            }
        }

        private void CmdWagonRemove(BasePlayer player)
        {
            if (!TryGetLookedAtWagon(player, out var wagon))
            {
                SendReply(player, "Look at a wagon to remove it.");
                return;
            }

            if (player.IsAdmin)
            {
                DestroyWagon(wagon, false, null);
                SendReply(player, "Wagon removed.");
                return;
            }

            if (wagon.OwnerID != player.userID)
            {
                SendReply(player, "You can only remove your own wagon.");
                return;
            }

            if (wagon.isTowed)
            {
                SendReply(player, "Detach your wagon before removing it.");
                return;
            }

            if (!HasInventorySpaceForRefund(player))
            {
                SendReply(player, "Inventory full. Make space before removing your wagon.");
                return;
            }

            RefundRequirements(player.inventory);
            DestroyWagon(wagon, false, null);
            SendReply(player, "Wagon removed and materials refunded.");
        }

        private List<string> GetMissingRequirements(PlayerInventory inventory)
        {
            var missing = new List<string>();
            if (inventory.GetAmount(_hqmDef.itemid) < RequiredHqm)
            {
                missing.Add("100 metal.refined");
            }

            if (inventory.GetAmount(_largeBoxDef.itemid) < RequiredLargeBoxes)
            {
                missing.Add("2 box.wooden.large");
            }

            if (inventory.GetAmount(_ropeDef.itemid) < RequiredRope)
            {
                missing.Add("10 rope");
            }

            return missing;
        }

        private void ConsumeRequirements(PlayerInventory inventory)
        {
            inventory.Take(null, _hqmDef.itemid, RequiredHqm);
            inventory.Take(null, _largeBoxDef.itemid, RequiredLargeBoxes);
            inventory.Take(null, _ropeDef.itemid, RequiredRope);
            inventory.ServerUpdate(0f);
        }

        private void RefundRequirements(PlayerInventory inventory)
        {
            inventory.GiveItem(ItemManager.CreateByName(_hqmDef.shortname, RequiredHqm));
            inventory.GiveItem(ItemManager.CreateByName(_largeBoxDef.shortname, RequiredLargeBoxes));
            inventory.GiveItem(ItemManager.CreateByName(_ropeDef.shortname, RequiredRope));
            inventory.ServerUpdate(0f);
        }

        private bool TrySpawnWagon(BasePlayer player, out WagonData wagon)
        {
            wagon = null;

            var framePos = player.transform.position + FrameSpawnOffset;
            var frame = GameManager.server.CreateEntity(FramePrefab, framePos, Quaternion.identity, true);
            if (frame == null)
            {
                return false;
            }

            frame.enableSaving = false;
            frame.OwnerID = player.userID;
            frame.Spawn();

            var rigid = frame.GetComponent<Rigidbody>();
            if (rigid != null)
            {
                rigid.isKinematic = true;
                rigid.useGravity = false;
            }

            var car = frame.GetComponent<ModularCar>();
            if (car != null)
            {
                car.enabled = false;
            }

            frame.SetFlag(BaseEntity.Flags.Reserved8, true);

            var box1 = GameManager.server.CreateEntity(LargeBoxPrefab, framePos + LeftBoxOffset, Quaternion.identity, true);
            var box2 = GameManager.server.CreateEntity(LargeBoxPrefab, framePos + RightBoxOffset, Quaternion.identity, true);

            if (box1 == null || box2 == null)
            {
                box1?.Kill();
                box2?.Kill();
                frame?.Kill();
                return false;
            }

            box1.enableSaving = false;
            box2.enableSaving = false;

            box1.OwnerID = player.userID;
            box2.OwnerID = player.userID;

            box1.Spawn();
            box2.Spawn();

            box1.SetParent(frame);
            box2.SetParent(frame);

            box1.transform.localPosition = LeftBoxOffset;
            box2.transform.localPosition = RightBoxOffset;
            box1.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            box2.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

            box1.SendNetworkUpdateImmediate();
            box2.SendNetworkUpdateImmediate();
            frame.SendNetworkUpdateImmediate();

            wagon = new WagonData
            {
                OwnerID = player.userID,
                wagonFrame = frame,
                storageBox1 = box1,
                storageBox2 = box2,
                horse = null,
                isTowed = false,
                LastUseTime = 0f
            };

            return true;
        }

        private void TrackWagon(WagonData wagon)
        {
            if (wagon == null || wagon.wagonFrame == null || wagon.wagonFrame.IsDestroyed)
            {
                return;
            }

            _wagons[wagon.wagonFrame] = wagon;
            _wagonsByOwner[wagon.OwnerID] = wagon;

            RegisterEntity(wagon.wagonFrame, wagon);
            RegisterEntity(wagon.storageBox1, wagon);
            RegisterEntity(wagon.storageBox2, wagon);
        }

        private void RegisterEntity(BaseEntity entity, WagonData wagon)
        {
            if (entity == null || entity.net == null)
            {
                return;
            }

            _wagonsByEntityId[entity.net.ID.Value] = wagon;
        }

        private void UnregisterEntity(BaseEntity entity)
        {
            if (entity == null || entity.net == null)
            {
                return;
            }

            _wagonsByEntityId.Remove(entity.net.ID.Value);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || !player.serverInput.WasJustPressed(BUTTON.USE))
            {
                return;
            }

            var horse = GetMountedHorse(player);
            if (horse == null)
            {
                return;
            }

            if (!IsLookingBackward(player, horse))
            {
                return;
            }

            if (_wagonsByOwner.TryGetValue(player.userID, out var wagon) == false || wagon == null)
            {
                return;
            }

            if (wagon.wagonFrame == null || wagon.wagonFrame.IsDestroyed)
            {
                return;
            }

            if (Time.time - wagon.LastUseTime < UseCooldown)
            {
                return;
            }

            wagon.LastUseTime = Time.time;

            if (wagon.isTowed)
            {
                if (wagon.horse == null || wagon.horse.IsDestroyed)
                {
                    wagon.isTowed = false;
                    wagon.horse = null;
                    return;
                }

                if (wagon.horse != horse)
                {
                    SendReply(player, "Your wagon is attached to another horse.");
                    return;
                }

                DetachWagon(wagon);
                SendReply(player, "Wagon detached.");
                return;
            }

            if (IsHorseTowing(horse))
            {
                SendReply(player, "This horse is already towing a wagon.");
                return;
            }

            var hitchPoint = horse.transform.position + horse.transform.TransformDirection(TowedOffset);
            var distance = Vector3.Distance(wagon.wagonFrame.transform.position, hitchPoint);
            if (distance > AttachDistance)
            {
                SendReply(player, "Move the horse closer to your wagon's hitch point.");
                return;
            }

            AttachWagon(horse, wagon);
            SendReply(player, "Wagon attached.");
        }

        private void OnTick()
        {
            if (_wagons.Count == 0)
            {
                return;
            }

            foreach (var wagon in _wagons.Values)
            {
                if (wagon == null || !wagon.isTowed)
                {
                    continue;
                }

                if (wagon.wagonFrame == null || wagon.wagonFrame.IsDestroyed)
                {
                    continue;
                }

                if (wagon.horse == null || wagon.horse.IsDestroyed)
                {
                    DestroyWagon(wagon, false, null);
                    continue;
                }

                var targetPos = wagon.horse.transform.position + wagon.horse.transform.TransformDirection(TowedOffset);
                var currentPos = wagon.wagonFrame.transform.position;
                wagon.wagonFrame.transform.position = Vector3.Lerp(currentPos, targetPos, Time.deltaTime * FollowSpeed);

                var targetRot = wagon.horse.transform.rotation;
                var currentRot = wagon.wagonFrame.transform.rotation;
                wagon.wagonFrame.transform.rotation = Quaternion.Slerp(currentRot, targetRot, Time.deltaTime * TurnSpeed);

                wagon.wagonFrame.SendNetworkUpdate();
            }
        }

        private void AttachWagon(BaseEntity horse, WagonData wagon)
        {
            if (wagon == null || wagon.wagonFrame == null || wagon.wagonFrame.IsDestroyed)
            {
                return;
            }

            wagon.horse = horse;
            wagon.isTowed = true;
        }

        private void DetachWagon(WagonData wagon)
        {
            if (wagon == null)
            {
                return;
            }

            wagon.horse = null;
            wagon.isTowed = false;
        }

        private bool IsHorseTowing(BaseEntity horse)
        {
            if (horse == null || horse.IsDestroyed)
            {
                return false;
            }

            foreach (var wagon in _wagons.Values)
            {
                if (wagon != null && wagon.isTowed && wagon.horse == horse)
                {
                    return true;
                }
            }

            return false;
        }

        private RidableHorse GetMountedHorse(BasePlayer player)
        {
            var mountable = player.GetMounted();
            if (mountable == null)
            {
                return null;
            }

            return mountable.GetComponentInParent<RidableHorse>();
        }

        private bool IsLookingBackward(BasePlayer player, RidableHorse horse)
        {
            var lookForward = player.eyes?.BodyForward() ?? player.transform.forward;
            return Vector3.Dot(lookForward.normalized, horse.transform.forward.normalized) < -0.2f;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (player == null || container == null || container.net == null)
            {
                return null;
            }

            if (!_wagonsByEntityId.TryGetValue(container.net.ID.Value, out var wagon) || wagon == null)
            {
                return null;
            }

            if (player.userID == wagon.OwnerID)
            {
                return null;
            }

            if (AreTeammates(player.userID, wagon.OwnerID))
            {
                return null;
            }

            return false;
        }

        private bool AreTeammates(ulong userA, ulong userB)
        {
            var teamA = RelationshipManager.ServerInstance?.FindPlayersTeam(userA);
            return teamA != null && teamA.members.Contains(userB);
        }

        private bool TryGetLookedAtWagon(BasePlayer player, out WagonData wagon)
        {
            wagon = null;

            var ray = player.eyes.HeadRay();
            if (!Physics.Raycast(ray, out var hit, RemoveDistance))
            {
                return false;
            }

            var entity = hit.GetEntity();
            if (entity == null || entity.net == null)
            {
                return false;
            }

            return _wagonsByEntityId.TryGetValue(entity.net.ID.Value, out wagon) && wagon != null;
        }

        private bool HasInventorySpaceForRefund(BasePlayer player)
        {
            if (player == null || player.inventory == null)
            {
                return false;
            }

            var neededSlots = 0;

            if (!CanStackInInventory(player.inventory, _hqmDef, RequiredHqm))
            {
                neededSlots += Mathf.CeilToInt((float)RequiredHqm / _hqmDef.stackable);
            }

            if (!CanStackInInventory(player.inventory, _ropeDef, RequiredRope))
            {
                neededSlots += Mathf.CeilToInt((float)RequiredRope / _ropeDef.stackable);
            }

            neededSlots += RequiredLargeBoxes; // stack size 1

            return CountFreeSlots(player.inventory) >= neededSlots;
        }

        private bool CanStackInInventory(PlayerInventory inventory, ItemDefinition def, int amount)
        {
            var remaining = amount;
            remaining -= GetStackSpace(inventory.containerMain, def);
            if (remaining <= 0) return true;
            remaining -= GetStackSpace(inventory.containerBelt, def);
            if (remaining <= 0) return true;
            remaining -= GetStackSpace(inventory.containerWear, def);
            return remaining <= 0;
        }

        private int GetStackSpace(ItemContainer container, ItemDefinition def)
        {
            if (container == null || def == null)
            {
                return 0;
            }

            var space = 0;
            foreach (var item in container.itemList)
            {
                if (item.info.itemid != def.itemid)
                {
                    continue;
                }

                space += Mathf.Max(0, def.stackable - item.amount);
            }

            return space;
        }

        private int CountFreeSlots(PlayerInventory inventory)
        {
            return CountFreeSlots(inventory.containerMain) + CountFreeSlots(inventory.containerBelt) + CountFreeSlots(inventory.containerWear);
        }

        private int CountFreeSlots(ItemContainer container)
        {
            if (container == null)
            {
                return 0;
            }

            return container.capacity - container.itemList.Count;
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            if (!(networkable is BaseEntity entity) || entity.net == null)
            {
                return;
            }

            if (!_wagonsByEntityId.TryGetValue(entity.net.ID.Value, out var wagon) || wagon == null)
            {
                return;
            }

            DestroyWagon(wagon, false, null);
        }

        private void DestroyWagon(WagonData wagon, bool refund, BasePlayer refundTarget)
        {
            if (wagon == null)
            {
                return;
            }

            UnregisterEntity(wagon.wagonFrame);
            UnregisterEntity(wagon.storageBox1);
            UnregisterEntity(wagon.storageBox2);

            _wagonsByOwner.Remove(wagon.OwnerID);
            if (wagon.wagonFrame != null)
            {
                _wagons.Remove(wagon.wagonFrame);
            }

            wagon.horse = null;
            wagon.isTowed = false;

            if (refund && refundTarget != null)
            {
                RefundRequirements(refundTarget.inventory);
            }

            wagon.storageBox1?.Kill();
            wagon.storageBox2?.Kill();
            wagon.wagonFrame?.Kill();
        }

        private void RemoveAllWagons()
        {
            var all = new List<WagonData>(_wagons.Values);
            foreach (var wagon in all)
            {
                DestroyWagon(wagon, false, null);
            }

            _wagons.Clear();
            _wagonsByOwner.Clear();
            _wagonsByEntityId.Clear();
        }

        private void Unload()
        {
            foreach (var wagon in _wagons.Values)
            {
                wagon?.wagonFrame?.Kill();
                wagon?.storageBox1?.Kill();
                wagon?.storageBox2?.Kill();
            }

            _wagons.Clear();
            _wagonsByOwner.Clear();
            _wagonsByEntityId.Clear();
        }
    }
}