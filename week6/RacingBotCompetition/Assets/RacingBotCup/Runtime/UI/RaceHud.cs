using RacingBotCup.Racing;
using RacingBotCup.Vehicle;
using UnityEngine;
using UnityEngine.UI;

namespace RacingBotCup.UI
{
    /// <summary>
    /// Speed and lap clock for whichever car is currently being driven.
    ///
    /// Builds its own canvas at runtime rather than relying on a prefab wired up in the scene:
    /// there is nothing to reconnect if a competitor rebuilds the scene, and no font asset to
    /// import. The car is found the same way the chase camera finds it — by height — so the readout
    /// follows the ghost or the competitor's car without being told which is which.
    /// </summary>
    public sealed class RaceHud : MonoBehaviour
    {
        /// <summary>Speed the bar treats as full scale, in m/s. Roughly the car's top speed.</summary>
        const float k_FullScale = 50f;

        [SerializeField] CarController m_Car;

        [Tooltip("비워 두면 현재 주행 중인 차를 자동으로 찾습니다")]
        [SerializeField] bool m_AutoFindCar = true;

        [Tooltip("랩 타임 표시")]
        [SerializeField] bool m_ShowTimer = true;

        [Tooltip("조향·스로틀 입력을 WASD 막대로 표시")]
        [SerializeField] bool m_ShowInputKeys = true;

        static readonly Color k_Dim = new Color(1f, 1f, 1f, 0.7f);
        static readonly Color k_OffTrack = new Color(1f, 0.45f, 0.35f);
        static readonly Color k_LapDone = new Color(0.55f, 0.95f, 0.55f);
        static readonly Color k_InputFill = new Color(0.35f, 0.78f, 0.95f, 0.95f);

        Text m_Readout;
        Text m_Caption;
        Text m_Timer;
        Text m_LapTime;
        RectTransform m_BarFill;
        RectTransform m_KeyWFill;
        RectTransform m_KeyAFill;
        RectTransform m_KeySFill;
        RectTransform m_KeyDFill;
        Eval.ChaseCamera m_Chase;
        RaceClock m_Clock;
        float m_NextSearch;

        void Start()
        {
            Build();
        }

        void Update()
        {
            if (m_AutoFindCar)
            {
                AcquireCar();
            }

            if (m_Car == null || m_Readout == null)
            {
                return;
            }

            var kilometresPerHour = Mathf.Abs(m_Car.ForwardSpeed) * 3.6f;
            m_Readout.text = Mathf.RoundToInt(kilometresPerHour).ToString();

            var fill = Mathf.Clamp01(Mathf.Abs(m_Car.ForwardSpeed) / k_FullScale);
            m_BarFill.anchorMax = new Vector2(fill, 1f);

            // Red once the tyres are on gravel — the readout doubles as an "you are off" light.
            var offTrack = m_Car.WheelsOffTrack > 0;
            m_Readout.color = offTrack ? k_OffTrack : Color.white;
            m_Caption.text = offTrack ? "OFF TRACK" : "km/h";

            UpdateTimer();
            UpdateInputKeys();
        }

        /// <summary>
        /// Fills the four key bars from the car's actual continuous input rather than any real
        /// keyboard — this reads the same <see cref="CarController.LastSteerInput"/> and
        /// <see cref="CarController.LastThrottleInput"/> whether they came from a human on WASD or a
        /// trained policy, so watching a policy shows exactly how hard it is turning and on the gas,
        /// the same way a human's key presses would.
        /// </summary>
        void UpdateInputKeys()
        {
            if (m_KeyWFill == null)
            {
                return;
            }

            var throttle = m_Car.LastThrottleInput;
            var steer = m_Car.LastSteerInput;

            SetKeyFill(m_KeyWFill, throttle);
            SetKeyFill(m_KeySFill, -throttle);
            SetKeyFill(m_KeyAFill, -steer);
            SetKeyFill(m_KeyDFill, steer);
        }

        static void SetKeyFill(RectTransform fill, float amount)
        {
            fill.anchorMax = new Vector2(1f, Mathf.Clamp01(amount));
        }

        void UpdateTimer()
        {
            if (m_Timer == null)
            {
                return;
            }

            if (m_Clock == null)
            {
                m_Timer.text = RaceClock.Format(0f);
                m_LapTime.text = "";
                return;
            }

            // Once the lap is in, the running clock is frozen anyway — showing the finished time in
            // green is what tells you at a glance that this car is done rather than merely stopped.
            if (m_Clock.IsFinished)
            {
                m_Timer.text = RaceClock.Format(m_Clock.LastLap);
                m_Timer.color = k_LapDone;
                m_LapTime.text = "LAP COMPLETE";
                return;
            }

            m_Timer.text = RaceClock.Format(m_Clock.Elapsed);
            m_Timer.color = Color.white;

            // Training rolls straight into the next episode, so the previous lap is the only thing
            // telling you whether this one is going better.
            m_LapTime.text = m_Clock.HasLap
                ? $"LAST {RaceClock.Format(m_Clock.LastLap)}"
                : "LAP TIME";
        }

        void AcquireCar()
        {
            // Show the readouts for whatever the camera is looking at. With ten environments running
            // at once, picking any car off the scene would readily land on one in another postcode.
            if (m_Chase == null)
            {
                m_Chase = FindFirstObjectByType<Eval.ChaseCamera>();
            }

            if (m_Chase != null && m_Chase.Target != null)
            {
                SetCar(m_Chase.Target);
                return;
            }

            if (m_Car != null && Eval.ChaseCamera.IsLiveAgentCar(m_Car))
            {
                return;
            }

            if (Time.unscaledTime < m_NextSearch)
            {
                return;
            }

            m_NextSearch = Time.unscaledTime + 0.25f;

            foreach (var car in FindObjectsByType<CarController>(FindObjectsSortMode.None))
            {
                if (Eval.ChaseCamera.IsLiveAgentCar(car))
                {
                    SetCar(car);
                    return;
                }
            }
        }

        void SetCar(CarController car)
        {
            if (m_Car == car && m_Clock != null)
            {
                return;
            }

            m_Car = car;

            // The clock is attached when the car is placed on a circuit, so a car sitting in an
            // unstarted scene legitimately has none.
            m_Clock = car != null ? car.GetComponent<RaceClock>() : null;
        }

        void Build()
        {
            var canvasObject = new GameObject("RaceHudCanvas");
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            BuildSpeedometer(canvasObject.transform);

            if (m_ShowInputKeys)
            {
                BuildInputKeys(canvasObject.transform);
            }

            if (m_ShowTimer)
            {
                BuildTimer(canvasObject.transform);
            }
        }

        void BuildSpeedometer(Transform parent)
        {
            var panel = CreatePanel("Speedometer", parent, new Vector2(1f, 0f), new Vector2(-40f, 40f),
                new Vector2(300f, 140f));

            m_Readout = CreateText("Readout", panel, 72, TextAnchor.LowerRight);
            var readoutRect = m_Readout.rectTransform;
            readoutRect.anchorMin = new Vector2(0f, 0.3f);
            readoutRect.anchorMax = new Vector2(1f, 1f);
            readoutRect.offsetMin = new Vector2(16f, 0f);
            readoutRect.offsetMax = new Vector2(-16f, -8f);

            m_Caption = CreateText("Caption", panel, 24, TextAnchor.UpperRight);
            var captionRect = m_Caption.rectTransform;
            captionRect.anchorMin = new Vector2(0f, 0.12f);
            captionRect.anchorMax = new Vector2(1f, 0.32f);
            captionRect.offsetMin = new Vector2(16f, 0f);
            captionRect.offsetMax = new Vector2(-16f, 0f);
            m_Caption.text = "km/h";
            m_Caption.color = k_Dim;

            var barBack = CreateRect("BarBackground", panel);
            barBack.anchorMin = new Vector2(0f, 0f);
            barBack.anchorMax = new Vector2(1f, 0.12f);
            barBack.offsetMin = new Vector2(16f, 12f);
            barBack.offsetMax = new Vector2(-16f, 0f);
            barBack.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);

            m_BarFill = CreateRect("BarFill", barBack);
            m_BarFill.anchorMin = Vector2.zero;
            m_BarFill.anchorMax = new Vector2(0f, 1f);
            m_BarFill.offsetMin = Vector2.zero;
            m_BarFill.offsetMax = Vector2.zero;
            m_BarFill.gameObject.AddComponent<Image>().color = new Color(0.95f, 0.78f, 0.25f, 0.95f);
        }

        /// <summary>
        /// Four key-shaped sliders in the physical WASD layout, sitting directly above the
        /// speedometer. Each fills from the bottom by how hard that input is currently pressed —
        /// full for a keyboard tap, partial for whatever fraction a policy's continuous output
        /// represents.
        /// </summary>
        void BuildInputKeys(Transform parent)
        {
            const float keySize = 60f;
            const float gap = 10f;
            const float padding = 12f;
            var panelHeight = padding * 2f + keySize * 2f + gap;

            var panel = CreatePanel("InputKeys", parent, new Vector2(1f, 0f),
                new Vector2(-40f, 40f + 140f + 12f), new Vector2(300f, panelHeight));

            var gridWidth = keySize * 3f + gap * 2f;
            var left = (300f - gridWidth) * 0.5f;
            var bottomRow = padding;
            var topRow = padding + keySize + gap;

            m_KeyWFill = CreateKey("W", panel, new Vector2(left + keySize + gap, topRow), keySize);
            m_KeyAFill = CreateKey("A", panel, new Vector2(left, bottomRow), keySize);
            m_KeySFill = CreateKey("S", panel, new Vector2(left + keySize + gap, bottomRow), keySize);
            m_KeyDFill = CreateKey("D", panel, new Vector2(left + (keySize + gap) * 2f, bottomRow), keySize);
        }

        /// <summary>
        /// One key: a dark square anchored by its bottom-left corner at <paramref name="bottomLeft"/>,
        /// a fill that grows upward from the bottom, and the letter on top of both. Returns the fill
        /// so the caller can drive it every frame.
        /// </summary>
        static RectTransform CreateKey(string letter, Transform parent, Vector2 bottomLeft, float size)
        {
            var key = CreateRect("Key" + letter, parent);
            key.anchorMin = Vector2.zero;
            key.anchorMax = Vector2.zero;
            key.pivot = Vector2.zero;
            key.anchoredPosition = bottomLeft;
            key.sizeDelta = new Vector2(size, size);
            key.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);

            var fill = CreateRect("Fill", key);
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = new Vector2(1f, 0f);
            fill.offsetMin = Vector2.zero;
            fill.offsetMax = Vector2.zero;
            fill.gameObject.AddComponent<Image>().color = k_InputFill;

            var label = CreateText(letter, key, 26, TextAnchor.MiddleCenter);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;
            label.text = letter;
            label.color = k_Dim;

            return fill;
        }

        void BuildTimer(Transform parent)
        {
            var panel = CreatePanel("LapTimer", parent, new Vector2(0.5f, 1f), new Vector2(0f, -32f),
                new Vector2(360f, 118f));

            m_Timer = CreateText("Time", panel, 56, TextAnchor.LowerCenter);
            var timerRect = m_Timer.rectTransform;
            timerRect.anchorMin = new Vector2(0f, 0.28f);
            timerRect.anchorMax = new Vector2(1f, 1f);
            timerRect.offsetMin = new Vector2(12f, 0f);
            timerRect.offsetMax = new Vector2(-12f, -8f);
            m_Timer.text = RaceClock.Format(0f);

            m_LapTime = CreateText("Label", panel, 22, TextAnchor.UpperCenter);
            var labelRect = m_LapTime.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0.04f);
            labelRect.anchorMax = new Vector2(1f, 0.28f);
            labelRect.offsetMin = new Vector2(12f, 0f);
            labelRect.offsetMax = new Vector2(-12f, 0f);
            m_LapTime.text = "LAP TIME";
            m_LapTime.color = k_Dim;
        }

        /// <summary>Shared with <see cref="FinalsHud"/> — both build their canvas in code, and a
        /// dark rounded-off panel is the same panel in both.</summary>
        internal static RectTransform CreatePanel(
            string name, Transform parent, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            var panel = CreateRect(name, parent);
            panel.anchorMin = anchor;
            panel.anchorMax = anchor;
            panel.pivot = anchor;
            panel.anchoredPosition = offset;
            panel.sizeDelta = size;

            panel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.45f);
            return panel;
        }

        internal static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static Font s_UiFont;

        /// <summary>
        /// The font every readout is drawn in, shared with <see cref="FinalsHud"/>.
        ///
        /// The engine's built-in LegacyRuntime.ttf carries no Hangul, so a Korean label — or a
        /// Korean racer's name over their car — comes out as boxes. Unity can borrow a font from the
        /// machine instead: the names below are tried in order and the first one installed wins
        /// (macOS, then Windows, then the usual Linux packages), falling back to the built-in font
        /// so there is still something to draw with if none of them are there.
        /// </summary>
        internal static Font UiFont
        {
            get
            {
                if (s_UiFont == null)
                {
                    s_UiFont = Font.CreateDynamicFontFromOSFont(
                        new[]
                        {
                            "Apple SD Gothic Neo", "AppleGothic",
                            "Malgun Gothic", "맑은 고딕",
                            "Noto Sans CJK KR", "NanumGothic",
                        },
                        24);
                }

                return s_UiFont != null ? s_UiFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
        }

        internal static Text CreateText(string name, Transform parent, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.font = UiFont;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;

            // Text truncates by default, and truncation drops the whole line rather than clipping
            // it — a glyph one pixel taller than its box simply vanishes. Letting it overflow keeps
            // the readout on screen whatever the window is scaled to.
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }
    }
}
