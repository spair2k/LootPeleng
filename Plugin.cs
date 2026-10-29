using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
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
        public const string Version = "1.0.1";

        internal static ManualLogSource Log;

        // loot closer than this counts as "right under you": the whole ring lights up
        private const float AllQuadrantsMeters = 0.5f;

        private readonly Collider[] _hits = new Collider[64];

        private LocalPlayer _player;
        private Vector3 _lastForward = Vector3.forward;
        private float _nextScan;
        private int _mask;
        private bool _questNear;

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
            if (!Settings.Enabled.Value || Time.unscaledTime < _nextScan)
            {
                return;
            }

            _nextScan = Time.unscaledTime + Math.Max(0.05f, Settings.ScanIntervalMs.Value / 1000f);
            Scan();
        }

        private void OnGUI()
        {
            if (!Settings.Enabled.Value || _mask == 0)
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
            GUI.DrawTexture(new Rect(Screen.width * 0.5f - size * 0.5f,
                Screen.height * 0.5f - size * 0.5f, size, size), tex);
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

                // corpses only hold loot inside bags and vests, which the ring is not about
                if (loot is Corpse)
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
