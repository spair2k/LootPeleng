using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using EFT;
using EFT.Bots;
using EFT.Interactive;
using UnityEngine;

namespace LootPeleng
{
    // draws a small four-segment ring at the center of the screen whenever loose loot is within a
    // radius of the player. every 90 degree segment lights up when loot sits in that direction,
    // relative to where the player is looking. loose loot means items lying on the ground (including
    // quest items and dug-up stashes); containers, backpacks and corpses are not counted.
    // quest items light their segment in the active color with a one-second pulse no matter
    // where they sit: ground, tables, containers, safes, car cabins or corpses
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.q8b1q.lootpeleng";
        public const string Name = "Loot Peleng";
        public const string Version = "1.2.0";

        internal static ManualLogSource Log;

        // loot closer than this counts as "right under you": the whole ring lights up
        private const float AllQuadrantsMeters = 0.5f;

        private readonly Collider[] _hits = new Collider[64];

        private LocalPlayer _player;
        private Vector3 _lastForward = Vector3.forward;
        private float _nextScan;
        private int _mask;
        private bool _questNear;
        private bool _forceHideRing;

        // combat threat tracking: hide ring while under aim/fire threat
        private float _lastThreatTime = -1f;
        private float _threatCheckNext;

        // layer every dead body gets in Corpse.Init, and the layer the game moves the colliders
        // of a dead body's held weapon to; both death paths re-layer the weapon, so the layer set
        // marks it as part of the body. resolved once
        private int _deadbodyLayer = -1;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);

            Log.LogInfo("Ready. A four-segment ring appears at screen center when loose loot is within "
                + Settings.RadiusMeters.Value.ToString("0.#") + " m.");
        }

        private void OnDestroy()
        {
            Ring.Dispose();
        }

        private void Update()
        {
            if (!Settings.Enabled.Value)
            {
                _forceHideRing = false;
                _mask = 0;
                _questNear = false;
                return;
            }

            UpdateCombatThreat();

            if (_forceHideRing)
            {
                _mask = 0;
                _questNear = false;
                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + Math.Max(0.05f, Settings.ScanIntervalMs.Value / 1000f);
                }
                return;
            }

            if (Time.unscaledTime < _nextScan)
            {
                return;
            }

            _nextScan = Time.unscaledTime + Math.Max(0.05f, Settings.ScanIntervalMs.Value / 1000f);
            Scan();
        }

        private void OnGUI()
        {
            if (!Settings.Enabled.Value || _mask == 0 || _forceHideRing)
            {
                return;
            }

            // quest loot nearby: the active color pulses on a one-second cycle; every other
            // segment keeps its static look
            var activeAlpha = Settings.ActiveAlpha.Value;
            if (_questNear)
            {
                activeAlpha *= 0.35f + 0.65f * Mathf.PingPong(Time.unscaledTime, 1f);
            }

            var tex = Ring.Build(_mask, Settings.RingSizePx.Value,
                Settings.BaseColor.Value, Settings.BaseAlpha.Value,
                Settings.ActiveColor.Value, activeAlpha);

            var size = tex.width;
            var pos = GetRingPosition(size);
            GUI.DrawTexture(new Rect(pos.x - size * 0.5f, pos.y - size * 0.5f, size, size), tex);
        }

        private Vector2 GetRingPosition(int size)
        {
            var centerX = Screen.width * 0.5f;
            var centerY = Screen.height * 0.5f;

            if (Settings.RingAnchor.Value != 0)
            {
                // 0 = Center, 1 = BottomLeftSilhouette
                if (Settings.RingAnchor.Value == 1)
                {
                    var x = Settings.RingOffsetX.Value;
                    var y = Settings.RingOffsetY.Value;
                    // approximate silhouette area in bottom-left; keep configurable offset from a reasonable base
                    return new Vector2(x + size * 0.6f, centerY * 2f - y - size * 0.6f);
                }
            }

            return new Vector2(centerX + Settings.RingOffsetX.Value, centerY + Settings.RingOffsetY.Value);
        }

        private void UpdateCombatThreat()
        {
            _forceHideRing = false;
            if (!Settings.CombatHideEnabled.Value)
            {
                _lastThreatTime = -1f;
                return;
            }

            var now = Time.unscaledTime;
            if (now < _threatCheckNext)
            {
                // still in cooldown, keep hidden if recently threatened
                if (_lastThreatTime > 0f)
                {
                    var timeout = Settings.CombatHideSilenceTimeout.Value;
                    if (timeout < 1f) timeout = 1f;
                    if (now - _lastThreatTime < timeout)
                    {
                        _forceHideRing = true;
                    }
                    else
                    {
                        _lastThreatTime = -1f;
                    }
                }
                return;
            }

            _threatCheckNext = now + Math.Max(0.05f, Settings.CombatHideCheckIntervalMs.Value / 1000f);

            if (_player == null)
            {
                _player = FindObjectOfType<LocalPlayer>();
            }

            if (_player == null || _player.HealthController == null || _player.HealthController.IsAlive == false)
            {
                return;
            }

            var threat = IsUnderThreat();
            if (threat)
            {
                _lastThreatTime = now;
                _forceHideRing = true;
                return;
            }

            if (_lastThreatTime > 0f)
            {
                var timeout = Settings.CombatHideSilenceTimeout.Value;
                if (timeout < 1f) timeout = 1f;
                if (now - _lastThreatTime < timeout)
                {
                    _forceHideRing = true;
                }
                else
                {
                    _lastThreatTime = -1f;
                }
            }
        }

        private bool IsUnderThreat()
        {
            if (_player == null)
            {
                return false;
            }

            var pos = _player.Transform.position;
            BotOwner[] botOwners = null;
            try
            {
                botOwners = UnityEngine.Object.FindObjectsOfType<BotOwner>();
            }
            catch
            {
                botOwners = null;
            }

            if (botOwners == null)
            {
                return false;
            }

            for (int i = 0; i < botOwners.Length; i++)
            {
                var botOwner = botOwners[i];
                if (botOwner == null)
                {
                    continue;
                }

                IPlayer botPerson = null;
                try
                {
                    var t = botOwner.GetType();
                    botPerson = t.GetProperty("Person")?.GetValue(botOwner, null) as IPlayer;
                    if (botPerson == null)
                    {
                        botPerson = t.GetProperty("BotPlayer")?.GetValue(botOwner, null) as IPlayer;
                    }
                }
                catch
                {
                    botPerson = null;
                }

                if (botPerson == null || botPerson == _player)
                {
                    continue;
                }

                float dist = 999f;
                try
                {
                    dist = Vector3.Distance(botOwner.Position, pos);
                }
                catch
                {
                    try
                    {
                        var p = botOwner.GetType().GetProperty("Position");
                        if (p != null)
                        {
                            var v = p.GetValue(botOwner, null);
                            if (v is Vector3 vv) dist = Vector3.Distance(vv, pos);
                        }
                    }
                    catch { }
                }

                if (dist > 40f)
                {
                    continue;
                }

                try
                {
                    var tBo = botOwner.GetType();
                    var memory = tBo.GetProperty("Memory")?.GetValue(botOwner, null);
                    if (memory != null)
                    {
                        var goalEnemy = memory.GetType().GetProperty("GoalEnemy")?.GetValue(memory, null);
                        if (goalEnemy != null)
                        {
                            var goalPerson = goalEnemy.GetType().GetProperty("Person")?.GetValue(goalEnemy, null) as IPlayer;
                            if (goalPerson == _player)
                            {
                                // try to detect aiming
                                object aimingData = tBo.GetProperty("AimingData")?.GetValue(botOwner, null);
                                if (aimingData == null)
                                {
                                    var aimingManager = tBo.GetProperty("AimingManager")?.GetValue(botOwner, null);
                                    if (aimingManager != null)
                                    {
                                        aimingData = aimingManager.GetType().GetProperty("AimingData")?.GetValue(aimingManager, null);
                                    }
                                }

                                if (aimingData != null)
                                {
                                    var isAimingProp = aimingData.GetType().GetProperty("IsAiming");
                                    if (isAimingProp != null)
                                    {
                                        var val = isAimingProp.GetValue(aimingData, null);
                                        if (val is bool bAim && bAim)
                                        {
                                            return true;
                                        }
                                    }
                                }

                                if (dist < 25f)
                                {
                                    return true;
                                }
                            }
                        }
                    }

                    var lookSensor = tBo.GetProperty("LookSensor")?.GetValue(botOwner, null);
                    if (lookSensor != null)
                    {
                        var isVisibleMethod = lookSensor.GetType().GetMethod("IsVisible");
                        if (isVisibleMethod != null)
                        {
                            try
                            {
                                var res = isVisibleMethod.Invoke(lookSensor, new object[] { _player });
                                if (res is bool vis && vis && dist < 20f)
                                {
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return false;
        }

        // one sphere query around the player, every ScanIntervalMs: cheap even on big maps because
        // only the colliders near the player are touched
        private void Scan()
        {
            _mask = 0;
            _questNear = false;

            if (_player == null)
            {
                _player = FindObjectOfType<LocalPlayer>();
            }

            if (_player == null)
            {
                return;
            }

            var origin = _player.Transform.position;
            var forward = ResolveForward();
            var right = Vector3.Cross(Vector3.up, forward);
            var radius = Settings.RadiusMeters.Value;

            var count = Physics.OverlapSphereNonAlloc(origin, radius, _hits, -1, QueryTriggerInteraction.Collide);

            if (_deadbodyLayer < 0)
            {
                _deadbodyLayer = LayerMask.NameToLayer("Deadbody");
            }

            // the weapon a body holds in its hands is a world object of its own: it is not a child
            // of the corpse in the scene, its owner chain does not reach the body's equipment tree
            // (root=null), and its LootItem item is set without an owner via SetItemAndRigidbody.
            // the game moves all of that weapon's colliders (with mounted mods) to the Deadbody
            // layer on both death paths, so the layer set marks it as part of the body. collect
            // the transforms of every Deadbody-layer object that carries a LootItem: these are
            // exactly the held weapons near the player
            HashSet<Transform> heldWeapons = null;
            for (var i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit == null || _deadbodyLayer < 0 || hit.gameObject.layer != _deadbodyLayer)
                {
                    continue;
                }

                if (hit.GetComponentInParent<LootItem>() is not LootItem held)
                {
                    continue;
                }

                heldWeapons ??= new HashSet<Transform>();
                heldWeapons.Add(held.transform);
            }

            for (var i = 0; i < count; i++)
            {
                var collider = _hits[i];
                if (collider == null)
                {
                    continue;
                }

                var loot = collider.GetComponent<LootItem>()
                    ?? collider.GetComponentInParent<LootItem>()
                    ?? collider.GetComponentInChildren<LootItem>(true);

                if (loot == null)
                {
                    continue;
                }

                if (loot.Item == null)
                {
                    continue;
                }

                // quest items must show up no matter where they sit: on the ground, on tables,
                // inside containers, safes, cars or corpses; their segments pulse
                if (loot.Item.QuestItem)
                {
                    _mask |= 0xF;
                    _questNear = true;
                    continue;
                }

                // a body (scav, PMC, boss alike) owns one equipment tree whose root item is
                // InventoryEquipment: the backpack, the rig, clothes and the weapon in hands are
                // all children of that root, even when the collider that hit the sphere belongs
                // to a child object of the corpse rather than the corpse itself. a backpack or
                // rig worn by a body is an isolating container, not loose loot. quest loot inside
                // the tree must still show up, so the check runs after the QuestItem check
                if (IsCorpseLoot(loot))
                {
                    continue;
                }

                // the weapon held in the body's hands (and the mods mounted in it) are LootItems
                // of their own with colliders inside the body's hierarchy, while their owner chain
                // does not reach the equipment root. anything mounted on the corpse this way is
                // part of the corpse, not loose loot. ObservedCorpse derives from Corpse, so one
                // component check covers both
                if (loot is Corpse || loot is ObservedCorpse || IsMountedOnCorpse(loot))
                {
                    continue;
                }

                // a Deadbody-layer transform carrying a LootItem is a held weapon of a nearby
                // body, not ground loot; anything nested in that weapon's object tree, like a
                // collider of a mounted mod, is covered by the hierarchy walk
                if (IsWithinHeldWeapon(loot.transform, heldWeapons))
                {
                    continue;
                }

                // anything sitting inside a container (jackets, safes, boxes, car cabins)
                // is found through its parent, so keep the ring about loose loot only
                if (loot.GetComponentInParent<LootableContainer>() != null)
                {
                    continue;
                }

                if (Settings.IgnoreOwnDrops.Value && loot.LastOwner == (IPlayer)_player)
                {
                    continue;
                }

                var delta = loot.transform.position - origin;
                var distance = delta.magnitude;

                // a big collider can poke into the sphere while the item itself stands outside it
                if (distance > radius)
                {
                    continue;
                }

                delta.y = 0f;

                if (distance < AllQuadrantsMeters || delta.sqrMagnitude < 0.0001f)
                {
                    _mask = 0xF;
                    continue;
                }

                var angle = Mathf.Atan2(Vector3.Dot(delta, right), Vector3.Dot(delta, forward)) * Mathf.Rad2Deg;
                if (angle < 0f)
                {
                    angle += 360f;
                }

                var quadrant = Mathf.FloorToInt(angle / 90f) % 4;
                _mask |= 1 << quadrant;
            }
        }

        // true for anything logically worn by a body: the corpse's LootItem itself (its Item is
        // the body's InventoryEquipment root) and every item inside that tree, because the owner
        // controller's RootItem resolves to the same InventoryEquipment for all of them
        private static bool IsCorpseLoot(LootItem loot)
        {
            var root = loot.ItemOwner != null ? loot.ItemOwner.RootItem : null;
            return root is EFT.InventoryLogic.InventoryEquipment
                || loot.Item is EFT.InventoryLogic.InventoryEquipment;
        }

        // true when the item physically hangs inside a corpse's transform hierarchy: the weapon
        // attached to the body's hands and the mods mounted in that weapon
        private static bool IsMountedOnCorpse(LootItem loot)
        {
            var t = loot.transform;
            while (t != null)
            {
                if (t.GetComponent<Corpse>() != null)
                {
                    return true;
                }

                t = t.parent;
            }

            return false;
        }

        // true when the item sits anywhere inside a held weapon's object tree: the weapon itself
        // is a Deadbody-layer transform (collected before this loop) and a mod mounted in it, like
        // a suppressor or a scope, hangs deeper in the same tree
        private static bool IsWithinHeldWeapon(Transform t, HashSet<Transform> heldWeapons)
        {
            if (heldWeapons == null)
            {
                return false;
            }

            while (t != null)
            {
                if (heldWeapons.Contains(t))
                {
                    return true;
                }

                t = t.parent;
            }

            return false;
        }

        // the segments face the same way the player looks; looking straight up or down flattens the
        // vector to nothing, in which case the last usable yaw is kept
        private Vector3 ResolveForward()
        {
            var camera = Camera.main;
            var forward = camera != null ? camera.transform.forward : _player.Transform.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude > 0.01f)
            {
                _lastForward = forward.normalized;
            }

            return _lastForward;
        }
    }

    internal static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> RadiusMeters;
        public static ConfigEntry<int> RingSizePx;
        public static ConfigEntry<bool> IgnoreOwnDrops;
        public static ConfigEntry<string> BaseColor;
        public static ConfigEntry<float> BaseAlpha;
        public static ConfigEntry<string> ActiveColor;
        public static ConfigEntry<float> ActiveAlpha;
        public static ConfigEntry<float> ScanIntervalMs;

        // combat hide
        public static ConfigEntry<bool> CombatHideEnabled;
        public static ConfigEntry<bool> CombatHideHideWhenAimedAt;
        public static ConfigEntry<float> CombatHideSilenceTimeout;
        public static ConfigEntry<float> CombatHideCheckIntervalMs;

        // positioning
        public static ConfigEntry<int> RingAnchor; // 0 = center, 1 = bottom left silhouette
        public static ConfigEntry<int> RingOffsetX;
        public static ConfigEntry<int> RingOffsetY;

        internal static void Bind(ConfigFile config)
        {
            Enabled = Ordered(config, "General", "Enabled", true,
                "Draw the loot peleng ring at the center of the screen.", 70);

            RadiusMeters = config.Bind("General", "RadiusMeters", 2f,
                new ConfigDescription("Loot closer than this to the player lights the ring.",
                    new AcceptableValueRange<float>(0.5f, 10f),
                    new ConfigurationManagerAttributes { Order = 65 }));

            RingSizePx = config.Bind("General", "RingSizePx", 58,
                new ConfigDescription("Diameter of the ring in screen pixels.",
                    new AcceptableValueRange<int>(24, 128),
                    new ConfigurationManagerAttributes { Order = 60 }));

            IgnoreOwnDrops = Ordered(config, "General", "IgnoreOwnDrops", true,
                "Items you dropped or threw yourself do not light the ring.", 55);

            BaseColor = ColorChoice(config, "General", "BaseColor", "#FFFFFF",
                "Color of a ring segment with no loot in it.", 50);

            BaseAlpha = config.Bind("General", "BaseAlpha", 0.3f,
                new ConfigDescription("How see-through an inactive segment is: 0 is invisible, 1 is solid.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { Order = 49 }));

            ActiveColor = ColorChoice(config, "General", "ActiveColor", "#FFB800",
                "Color of a ring segment that has loot in it.", 45);

            ActiveAlpha = config.Bind("General", "ActiveAlpha", 1f,
                new ConfigDescription("How see-through a lit segment is: 0 is invisible, 1 is solid.",
                    new AcceptableValueRange<float>(0f, 1f),
                    new ConfigurationManagerAttributes { Order = 44 }));

            // fixed to 200ms (menu entry removed): how often the sphere query runs
            ScanIntervalMs = Hidden(config, "General", "ScanIntervalMs", 200f,
                "Fixed to 200ms: how often loot is scanned around the player.");

            // combat hide
            CombatHideEnabled = Ordered(config, "Combat Hide", "Enabled", true,
                "Completely hide the ring while under threat (even for quest items). Resume after silence.", 40);
            CombatHideHideWhenAimedAt = Ordered(config, "Combat Hide", "HideWhenAimedAt", true,
                "Hide if enemies are aiming at you or have you as a target.", 39);
            CombatHideSilenceTimeout = config.Bind("Combat Hide", "SilenceTimeoutSeconds", 4f,
                new ConfigDescription("Seconds of silence after threat before the ring returns.",
                    new AcceptableValueRange<float>(1f, 10f),
                    new ConfigurationManagerAttributes { Order = 38 }));
            CombatHideCheckIntervalMs = config.Bind("Combat Hide", "CheckIntervalMs", 100f,
                new ConfigDescription("How often to check for threats.",
                    new AcceptableValueRange<float>(50f, 250f),
                    new ConfigurationManagerAttributes { Order = 37 }));

            // positioning
            RingAnchor = config.Bind("Position", "Anchor", 0,
                new ConfigDescription("0 = center, 1 = bottom left near silhouette.",
                    new AcceptableValueRange<int>(0, 1),
                    new ConfigurationManagerAttributes { Order = 30 }));
            RingOffsetX = config.Bind("Position", "OffsetX", 0,
                new ConfigDescription("Horizontal offset from anchor in pixels.",
                    new AcceptableValueRange<int>(-512, 512),
                    new ConfigurationManagerAttributes { Order = 29 }));
            RingOffsetY = config.Bind("Position", "OffsetY", 0,
                new ConfigDescription("Vertical offset from anchor in pixels.",
                    new AcceptableValueRange<int>(-512, 512),
                    new ConfigurationManagerAttributes { Order = 28 }));

            // not a setting: a plain entry carrying the project page, visible in the menu and in
            // the cfg file, so anyone who opens either one can find the repository
            Ordered(config, "About", "Repository", "https://github.com/spair2k/LootPeleng",
                "Project page on GitHub: releases, source, issues.", 10);
        }

        // sections are listed in the menu in the order they are first bound; inside a section the
        // manager sorts by the Order tag, descending
        private static ConfigEntry<T> Ordered<T>(ConfigFile config, string section, string key, T defaultValue,
            string description, int order)
        {
            return config.Bind(section, key, defaultValue,
                new ConfigDescription(description, null, new ConfigurationManagerAttributes { Order = order }));
        }

        // ConfigurationManager orders a section by the Order tag on its entries (descending) and
        // reads Browsable/CustomDrawer off the same attributes object. it is found by type name,
        // so a matching local class keeps the plugin independent of the manager assembly
        public sealed class ConfigurationManagerAttributes
        {
            public int Order;

            // hides the entry from the manager menu while the setting keeps working in the config file
            public bool Browsable = true;
            public Action<ConfigEntryBase> CustomDrawer;
        }

        private static readonly string[] ColorQuickChoices = new[]
        {
            "#FF3333", // red
            "#FFB800", // amber
            "#33FF66", // green
            "#33B5FF", // sky blue
            "#FFFFFF", // white
        };

        // compact color selector: preview swatch, hex field and five one-click preset checkboxes
        private static ConfigEntry<string> ColorChoice(ConfigFile config, string section, string key,
            string defaultValue, string description, int order)
        {
            return config.Bind(section, key, defaultValue,
                new ConfigDescription(description, null,
                    new ConfigurationManagerAttributes { Order = order, CustomDrawer = DrawColorSimple }));
        }

        private static void DrawColorSimple(ConfigEntryBase entry)
        {
            var configEntry = entry as ConfigEntry<string>;
            if (configEntry == null)
            {
                return;
            }

            var oldColor = GUI.color;
            Color previewColor = Color.white;
            ColorUtility.TryParseHtmlString(configEntry.Value, out previewColor);

            // preview + hex
            GUILayout.BeginHorizontal();
            GUI.color = previewColor;
            GUILayout.Label("■", GUILayout.Width(14));
            GUI.color = oldColor;
            var newHex = GUILayout.TextField(configEntry.Value ?? "#FFFFFF", GUILayout.Width(72));
            if (!string.Equals(newHex, configEntry.Value, StringComparison.OrdinalIgnoreCase) &&
                ColorUtility.TryParseHtmlString(newHex, out var parsed))
            {
                configEntry.Value = newHex.ToUpperInvariant();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // five unlabeled checkboxes in contrasting colors
            GUILayout.BeginHorizontal();
            for (var i = 0; i < ColorQuickChoices.Length; i++)
            {
                var hexChoice = ColorQuickChoices[i];
                Color boxColor;
                if (!ColorUtility.TryParseHtmlString(hexChoice, out boxColor))
                {
                    boxColor = Color.white;
                }

                var isSelected = string.Equals(configEntry.Value, hexChoice, StringComparison.OrdinalIgnoreCase);
                GUI.color = boxColor;
                var toggled = GUILayout.Toggle(isSelected, GUIContent.none, GUILayout.Width(16), GUILayout.Height(16));
                GUI.color = oldColor;

                if (toggled && !isSelected)
                {
                    configEntry.Value = hexChoice;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // a setting that keeps working but does not show up in the manager menu
        private static ConfigEntry<T> Hidden<T>(ConfigFile config, string section, string key, T defaultValue,
            string description)
        {
            return config.Bind(section, key, defaultValue,
                new ConfigDescription(description, null, new ConfigurationManagerAttributes { Browsable = false }));
        }
    }

    // the ring is drawn as a small texture rebuilt only when the lit segments or the settings change
    internal static class Ring
    {
        private static Texture2D _tex;
        private static Color32[] _pixels;
        private static int _size;
        private static int _mask = -1;
        private static string _baseHex = string.Empty;
        private static string _activeHex = string.Empty;
        private static float _baseAlpha = -1f;
        private static float _activeAlpha = -1f;

        public static Texture2D Build(int mask, int size, string baseHex, float baseAlpha,
            string activeHex, float activeAlpha)
        {
            size = Mathf.Clamp(size, 24, 128);
            baseAlpha = Mathf.Clamp01(baseAlpha);
            activeAlpha = Mathf.Clamp01(activeAlpha);

            if (_tex != null && mask == _mask && size == _size
                && baseHex == _baseHex && activeHex == _activeHex
                && baseAlpha == _baseAlpha && activeAlpha == _activeAlpha)
            {
                return _tex;
            }

            if (_tex == null || size != _size)
            {
                if (_tex != null)
                {
                    UnityEngine.Object.Destroy(_tex);
                }

                _tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Trilinear,
                };
                _pixels = new Color32[size * size];
                _size = size;
                _mask = -1;
            }

            var center = (size - 1) * 0.5f;
            var outer = center - 1f;
            var thickness = Mathf.Max(3f, size * 0.13f);
            var gapDegrees = 3f;

            // the hex fields hold the pure color, the sliders decide the see-through; any alpha
            // someone typed into the hex itself is overridden here
            var baseColor = WithAlpha(ParseColor(baseHex, Color.white), baseAlpha);
            var activeColor = WithAlpha(ParseColor(activeHex, Color.white), activeAlpha);

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    // the band between outer-thickness and outer, softened by half a pixel on each edge
                    var edge = Mathf.Min(dist - (outer - thickness), outer - dist);
                    var alpha = Mathf.Clamp01(edge + 0.5f);

                    if (alpha <= 0f)
                    {
                        _pixels[y * size + x] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    // screen up is the look direction, angles go clockwise from it
                    var angle = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                    if (angle < 0f)
                    {
                        angle += 360f;
                    }

                    var quadrant = Mathf.FloorToInt(angle / 90f) % 4;
                    var within = angle - quadrant * 90f;

                    if (within < gapDegrees * 0.5f || within > 90f - gapDegrees * 0.5f)
                    {
                        // keep a faint tick in the gaps so the split stays readable when off
                        _pixels[y * size + x] = Fade(baseColor, alpha * 0.35f);
                        continue;
                    }

                    var color = ((mask >> quadrant) & 1) == 1 ? activeColor : baseColor;
                    _pixels[y * size + x] = Fade(color, alpha);
                }
            }

            _tex.SetPixels32(_pixels);
            _tex.Apply(false);

            _mask = mask;
            _baseHex = baseHex;
            _activeHex = activeHex;
            _baseAlpha = baseAlpha;
            _activeAlpha = activeAlpha;
            return _tex;
        }

        public static void Dispose()
        {
            if (_tex != null)
            {
                UnityEngine.Object.Destroy(_tex);
                _tex = null;
            }
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        private static Color32 Fade(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, color.a * alpha);
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            Color color;
            return ColorUtility.TryParseHtmlString(hex, out color) ? color : fallback;
        }
    }
}
