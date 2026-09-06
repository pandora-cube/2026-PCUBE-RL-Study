using System.Collections.Generic;
using RacingBotCup.Finals;
using RacingBotCup.Racing;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RacingBotCup.UI
{
    /// <summary>
    /// The finals broadcast overlay: a name over every car, a live standings board that can be
    /// clicked to change who the camera follows, and the points table between circuits.
    ///
    /// Built in code for the same reason <see cref="RaceHud"/> is — nothing to rewire when the
    /// scene is regenerated, and no font asset to import.
    ///
    /// Clicks are read straight from <c>Mouse.current</c> and hit-tested against the row rects
    /// rather than going through uGUI Buttons. The project is on the new Input System, where a
    /// Button needs an EventSystem with an InputSystemUIInputModule and an actions asset behind it;
    /// hit-testing six rectangles needs none of that and cannot be broken by a project setting.
    /// </summary>
    // Tags are projected through the camera, so this has to run after the chase camera has moved
    // it — otherwise every tag trails its car by a frame while the view is panning.
    [DefaultExecutionOrder(1000)]
    public sealed class FinalsHud : MonoBehaviour
    {
        [Tooltip("비워 두면 씬에서 찾습니다")]
        [SerializeField] FinalsDirector m_Director;

        const float k_RowHeight = 38f;
        const float k_BoardWidth = 460f;
        const float k_TagHeight = 2.4f;

        static readonly Color k_Panel = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color k_RowIdle = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color k_RowWatched = new Color(1f, 1f, 1f, 0.20f);
        static readonly Color k_Dim = new Color(1f, 1f, 1f, 0.7f);

        sealed class Row
        {
            public RectTransform Rect;
            public Image Background;
            public Text Position;
            public Text Name;
            public Text Time;
            public Text Points;
        }

        sealed class Tag
        {
            public RectTransform Rect;
            public Text Text;
        }

        readonly List<Row> m_Rows = new List<Row>();
        readonly List<Tag> m_Tags = new List<Tag>();

        /// <summary>Where this frame's tags ended up, so the next one can avoid them.</summary>
        readonly List<Vector2> m_PlacedTags = new List<Vector2>();

        RectTransform m_CanvasRect;
        RectTransform m_Board;
        Text m_BoardHeader;

        RectTransform m_Overlay;
        Text m_OverlayTitle;
        Text m_OverlayNames;
        Text m_OverlayTimes;
        Text m_OverlayPoints;
        RectTransform m_Button;
        Text m_ButtonLabel;

        Camera m_Camera;

        void Start()
        {
            if (m_Director == null)
            {
                m_Director = FindFirstObjectByType<FinalsDirector>();
            }

            Build();
        }

        void LateUpdate()
        {
            if (m_Director == null)
            {
                return;
            }

            var standings = m_Director.Standings;
            EnsureRows(standings.Count);

            UpdateBoard(standings);
            UpdateTags(standings);
            UpdateOverlay(standings);
            ReadInput(standings);
        }

        // ------------------------------------------------------------------
        // Live board
        // ------------------------------------------------------------------

        void UpdateBoard(IReadOnlyList<FinalsRacer> standings)
        {
            var racing = m_Director.CurrentPhase == FinalsDirector.Phase.Racing;
            m_Board.gameObject.SetActive(m_Director.CurrentPhase != FinalsDirector.Phase.Idle);

            m_BoardHeader.text =
                $"MAP {m_Director.MapNumber}/{m_Director.MapCount}   SEED {m_Director.CurrentSeed}   " +
                RaceClock.Format(m_Director.RaceTime);
            m_BoardHeader.color = racing ? Color.white : k_Dim;

            var leader = standings.Count > 0 && standings[0].State == RacerState.Finished
                ? standings[0].Time
                : 0f;

            for (var i = 0; i < m_Rows.Count; i++)
            {
                var row = m_Rows[i];
                var visible = i < standings.Count;
                row.Rect.gameObject.SetActive(visible);
                if (!visible)
                {
                    continue;
                }

                var racer = standings[i];
                row.Position.text = $"P{i + 1}";
                row.Name.text = racer.Name;
                row.Name.color = racer.Colour;
                row.Points.text = $"{racer.TotalPoints}";

                row.Time.text = DescribeTime(racer, leader);
                row.Time.color = racer.State == RacerState.Retired ? k_Dim : Color.white;

                row.Background.color = ReferenceEquals(racer, m_Director.Spectating)
                    ? k_RowWatched
                    : k_RowIdle;
            }
        }

        /// <summary>
        /// A finished car shows its lap time, and the gap to the leader once somebody else has set
        /// one. A car still out there shows how far round it is instead — a running clock says
        /// nothing about who is winning when everyone's clock reads the same.
        /// </summary>
        static string DescribeTime(FinalsRacer racer, float leaderTime)
        {
            switch (racer.State)
            {
                case RacerState.Finished:
                    var gap = racer.Time - leaderTime;
                    return gap > 0.0005f
                        ? $"{RaceClock.Format(racer.Time)}  +{gap:F2}"
                        : RaceClock.Format(racer.Time);

                case RacerState.Retired:
                    return "RETIRED";

                default:
                    return $"{racer.Distance:F0} m";
            }
        }

        // ------------------------------------------------------------------
        // Name tags
        // ------------------------------------------------------------------

        void UpdateTags(IReadOnlyList<FinalsRacer> standings)
        {
            if (m_Camera == null)
            {
                m_Camera = Camera.main;
            }

            m_PlacedTags.Clear();

            for (var i = 0; i < m_Tags.Count; i++)
            {
                var tag = m_Tags[i];
                var racer = i < standings.Count ? standings[i] : null;
                var car = racer?.Rig?.Car;

                // Only while a circuit is actually running: the results overlay is drawn under these
                // tags, and a name floating over a frozen car in the middle of the points table
                // reads as a rendering bug.
                if (m_Camera == null || car == null ||
                    m_Director.CurrentPhase != FinalsDirector.Phase.Racing)
                {
                    tag.Rect.gameObject.SetActive(false);
                    continue;
                }

                var screen = m_Camera.WorldToScreenPoint(
                    car.transform.position + Vector3.up * k_TagHeight);

                // Behind the camera, WorldToScreenPoint still returns a plausible-looking x/y —
                // mirrored. The z sign is the only thing that gives it away.
                if (screen.z <= 0f)
                {
                    tag.Rect.gameObject.SetActive(false);
                    continue;
                }

                tag.Rect.gameObject.SetActive(true);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    m_CanvasRect, screen, null, out var local);
                tag.Rect.anchoredPosition = Stack(local);

                tag.Text.text = $"P{racer.Position}  {racer.Name}";
                tag.Text.color = racer.State == RacerState.Retired ? k_Dim : racer.Colour;
            }
        }

        /// <summary>
        /// Nudges a tag down until it is clear of the ones already placed this frame, and remembers
        /// where it landed.
        ///
        /// Cars share a start pose and, for much of a lap, the same racing line, so without this the
        /// field's names pile into one unreadable smear exactly when it matters — the start, and
        /// every time somebody is caught. Tags are placed in race order, so the leader keeps the
        /// spot over their own car and whoever is behind gets stacked underneath.
        /// </summary>
        Vector2 Stack(Vector2 position)
        {
            const float rowHeight = 30f;
            const float sideBySide = 130f;

            var collided = true;
            while (collided)
            {
                collided = false;
                foreach (var placed in m_PlacedTags)
                {
                    if (Mathf.Abs(placed.x - position.x) < sideBySide &&
                        Mathf.Abs(placed.y - position.y) < rowHeight)
                    {
                        position.y = placed.y - rowHeight;
                        collided = true;
                        break;
                    }
                }
            }

            m_PlacedTags.Add(position);
            return position;
        }

        // ------------------------------------------------------------------
        // Between circuits
        // ------------------------------------------------------------------

        void UpdateOverlay(IReadOnlyList<FinalsRacer> standings)
        {
            var phase = m_Director.CurrentPhase;
            var showing = phase is FinalsDirector.Phase.Intermission or FinalsDirector.Phase.Finished;
            m_Overlay.gameObject.SetActive(showing);

            if (!showing)
            {
                return;
            }

            var final = phase == FinalsDirector.Phase.Finished;
            m_OverlayTitle.text = final
                ? "최종 결과"
                : $"MAP {m_Director.MapNumber} 결과  ·  중간 순위";

            // Three columns rather than one padded string: the built-in font is proportional, so
            // padding a name to a column width lines nothing up.
            var names = new System.Text.StringBuilder();
            var times = new System.Text.StringBuilder();
            var points = new System.Text.StringBuilder();

            foreach (var racer in standings)
            {
                names.Append($"P{racer.Position}   {racer.Name}\n");
                times.Append(racer.State == RacerState.Finished
                    ? RaceClock.Format(racer.Time) + "\n"
                    : "RETIRED\n");
                points.Append(final
                    ? $"{DescribeHistory(racer)} = {racer.TotalPoints}\n"
                    : $"+{racer.MapPoints}  →  {racer.TotalPoints}\n");
            }

            m_OverlayNames.text = names.ToString();
            m_OverlayTimes.text = times.ToString();
            m_OverlayPoints.text = points.ToString();

            m_Button.gameObject.SetActive(!final);
            m_ButtonLabel.text = $"다음 맵 ({m_Director.MapNumber + 1}/{m_Director.MapCount})  ▶   [Space]";
        }

        /// <summary>"6 + 0 + 4" — the whole event at a glance on the final table.</summary>
        static string DescribeHistory(FinalsRacer racer)
        {
            var parts = new List<string>(racer.History.Count);
            foreach (var record in racer.History)
            {
                parts.Add(record.Points.ToString());
            }

            return string.Join(" + ", parts);
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------

        void ReadInput(IReadOnlyList<FinalsRacer> standings)
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (m_Director.CurrentPhase == FinalsDirector.Phase.Intermission &&
                    (keyboard.spaceKey.wasPressedThisFrame || keyboard.enterKey.wasPressedThisFrame))
                {
                    m_Director.Continue();
                    return;
                }

                for (var i = 0; i < standings.Count && i < 6; i++)
                {
                    if (keyboard[Key.Digit1 + i].wasPressedThisFrame)
                    {
                        m_Director.Spectate(standings[i]);
                    }
                }
            }

            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
            {
                return;
            }

            var point = mouse.position.ReadValue();

            if (m_Button.gameObject.activeSelf &&
                RectTransformUtility.RectangleContainsScreenPoint(m_Button, point, null))
            {
                m_Director.Continue();
                return;
            }

            for (var i = 0; i < m_Rows.Count && i < standings.Count; i++)
            {
                if (m_Rows[i].Rect.gameObject.activeSelf &&
                    RectTransformUtility.RectangleContainsScreenPoint(m_Rows[i].Rect, point, null))
                {
                    m_Director.Spectate(standings[i]);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        void Build()
        {
            var canvasObject = new GameObject("FinalsHudCanvas");
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Above RaceHud's speedometer, which sits at 100.
            canvas.sortingOrder = 110;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            m_CanvasRect = (RectTransform)canvasObject.transform;

            BuildBoard(canvasObject.transform);
            BuildOverlay(canvasObject.transform);
        }

        void BuildBoard(Transform parent)
        {
            var height = 54f + k_RowHeight * FinalsScoring.MaxRacers + 34f;
            m_Board = RaceHud.CreatePanel("Standings", parent, new Vector2(0f, 1f),
                new Vector2(24f, -24f), new Vector2(k_BoardWidth, height));
            m_Board.GetComponent<Image>().color = k_Panel;

            m_BoardHeader = RaceHud.CreateText("Header", m_Board, 24, TextAnchor.MiddleLeft);
            Place(m_BoardHeader.rectTransform, 12f, -8f, k_BoardWidth - 24f, 34f);

            var hint = RaceHud.CreateText("Hint", m_Board, 18, TextAnchor.MiddleLeft);
            Place(hint.rectTransform, 12f, -(46f + k_RowHeight * FinalsScoring.MaxRacers + 8f),
                k_BoardWidth - 24f, 26f);
            hint.text = "행 클릭 · 1~6 키 : 관전 전환";
            hint.color = k_Dim;
        }

        /// <summary>
        /// Rows and tags are built once, for however many racers the first standings carry, and then
        /// reused — the field cannot change size mid-event.
        /// </summary>
        void EnsureRows(int count)
        {
            for (var i = m_Rows.Count; i < count; i++)
            {
                m_Rows.Add(BuildRow(i));
                m_Tags.Add(BuildTag(i));
            }
        }

        Row BuildRow(int index)
        {
            var rect = RaceHud.CreateRect($"Row{index}", m_Board);
            Place(rect, 8f, -(46f + k_RowHeight * index), k_BoardWidth - 16f, k_RowHeight - 4f);

            var row = new Row
            {
                Rect = rect,
                Background = rect.gameObject.AddComponent<Image>(),
                Position = RaceHud.CreateText("Pos", rect, 22, TextAnchor.MiddleLeft),
                Name = RaceHud.CreateText("Name", rect, 22, TextAnchor.MiddleLeft),
                Time = RaceHud.CreateText("Time", rect, 22, TextAnchor.MiddleRight),
                Points = RaceHud.CreateText("Points", rect, 22, TextAnchor.MiddleRight),
            };

            row.Background.color = k_RowIdle;

            Stretch(row.Position.rectTransform, 10f, 56f);
            Stretch(row.Name.rectTransform, 66f, 210f);
            Stretch(row.Time.rectTransform, 210f, 396f);
            Stretch(row.Points.rectTransform, 396f, k_BoardWidth - 24f);
            return row;
        }

        Tag BuildTag(int index)
        {
            var rect = RaceHud.CreateRect($"Tag{index}", m_CanvasRect);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(260f, 34f);

            var text = RaceHud.CreateText("Label", rect, 24, TextAnchor.MiddleCenter);
            Stretch(text.rectTransform, 0f, 260f);

            // A white-on-white name over a pale car is unreadable without it.
            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(2f, -2f);

            return new Tag { Rect = rect, Text = text };
        }

        void BuildOverlay(Transform parent)
        {
            m_Overlay = RaceHud.CreateRect("Intermission", parent);
            m_Overlay.anchorMin = Vector2.zero;
            m_Overlay.anchorMax = Vector2.one;
            m_Overlay.offsetMin = Vector2.zero;
            m_Overlay.offsetMax = Vector2.zero;
            m_Overlay.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.78f);

            var box = RaceHud.CreatePanel("Box", m_Overlay, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(920f, 560f));
            box.GetComponent<Image>().color = new Color(0.06f, 0.07f, 0.09f, 0.95f);

            m_OverlayTitle = RaceHud.CreateText("Title", box, 44, TextAnchor.MiddleCenter);
            Place(m_OverlayTitle.rectTransform, 0f, -30f, 920f, 60f);

            m_OverlayNames = BuildColumn("Names", box, 70f, 320f, TextAnchor.UpperLeft);
            m_OverlayTimes = BuildColumn("Times", box, 400f, 200f, TextAnchor.UpperRight);
            m_OverlayPoints = BuildColumn("Points", box, 630f, 230f, TextAnchor.UpperRight);

            m_Button = RaceHud.CreateRect("NextButton", box);
            Place(m_Button, 260f, -470f, 400f, 62f);
            m_Button.gameObject.AddComponent<Image>().color = new Color(0.20f, 0.45f, 0.85f, 0.95f);

            m_ButtonLabel = RaceHud.CreateText("Label", m_Button, 26, TextAnchor.MiddleCenter);
            Stretch(m_ButtonLabel.rectTransform, 0f, 400f);

            m_Overlay.gameObject.SetActive(false);
        }

        /// <summary>One column of the result table. All three share a line height, so the rows read
        /// across.</summary>
        static Text BuildColumn(string name, Transform parent, float x, float width, TextAnchor alignment)
        {
            var text = RaceHud.CreateText(name, parent, 28, alignment);
            Place(text.rectTransform, x, -110f, width, 330f);
            text.lineSpacing = 1.35f;
            return text;
        }

        /// <summary>Positions a child by its top-left corner, in the parent's pixels.</summary>
        static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>Spans a column of the parent, full height.</summary>
        static void Stretch(RectTransform rect, float left, float right)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(right - left, 0f);
            rect.anchoredPosition = new Vector2(left, 0f);
        }
    }
}
