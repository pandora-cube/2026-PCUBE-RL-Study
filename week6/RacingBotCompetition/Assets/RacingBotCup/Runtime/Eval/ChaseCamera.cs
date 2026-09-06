using RacingBotCup.Agent;
using RacingBotCup.Racing;
using RacingBotCup.Vehicle;
using UnityEngine;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// Follows whichever car is currently being timed, so a run can actually be watched.
    ///
    /// The harness keeps both cars loaded and parks the idle one far below the circuit, so the
    /// camera picks its subject by height rather than by name — that way it switches automatically
    /// from the baseline lap to the policy lap without the session having to tell it anything.
    ///
    /// Purely observational: it runs in LateUpdate and touches nothing the simulation reads, so
    /// watching a run cannot change its result.
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        [Tooltip("Metres behind the car.")]
        [SerializeField] float m_Distance = 12f;

        [Tooltip("Metres above the car.")]
        [SerializeField] float m_Height = 5f;

        [Tooltip("Metres ahead of the car to aim at.")]
        [SerializeField] float m_LookAhead = 8f;

        [Tooltip("0 = snap instantly, higher = smoother. Seconds to catch up.")]
        [SerializeField] float m_Smoothing = 0.18f;

        [Tooltip("Turn off to keep the fixed overview camera instead.")]
        // Not named m_Enabled: MonoBehaviour already serialises a field by that name, and Unity
        // rejects the whole component when a subclass shadows it.
        [SerializeField] bool m_Follow = true;

        CarController m_Target;
        CarController m_Pinned;
        Vector3 m_Velocity;
        float m_NextSearch;

        /// <summary>The car currently being followed, so other displays can agree with the view.</summary>
        public CarController Target => m_Target;

        /// <summary>
        /// Follows one specific car until told otherwise; null hands the choice back to
        /// <see cref="AcquireTarget"/>.
        ///
        /// Automatic acquisition assumes there is only one competitor's car actually racing, which
        /// is true of an evaluation and false of the finals — with a field of six it would pick an
        /// arbitrary one and cut to a different car every re-search. Spectating is the finals'
        /// answer: the operator says who to watch.
        /// </summary>
        public void Spectate(CarController car)
        {
            m_Pinned = car;

            if (car != null)
            {
                m_Target = car;
            }
        }

        void LateUpdate()
        {
            if (!m_Follow)
            {
                return;
            }

            AcquireTarget();
            if (m_Target == null)
            {
                return;
            }

            var car = m_Target.transform;
            var desired = car.position - car.forward * m_Distance + Vector3.up * m_Height;

            transform.position = m_Smoothing > 0f
                ? Vector3.SmoothDamp(transform.position, desired, ref m_Velocity, m_Smoothing)
                : desired;

            transform.LookAt(car.position + car.forward * m_LookAhead + Vector3.up * 1.5f);
        }

        /// <summary>
        /// Picks the competitor's car that is actually still racing right now — never the baseline
        /// ghost, and never a finished car left sitting where an earlier seed ended. Re-checked a
        /// few times a second because the harness swaps cars between laps and between seeds.
        /// </summary>
        void AcquireTarget()
        {
            // A pinned car is watched whatever it is doing — including sitting still, having already
            // finished its lap, which IsLiveAgentCar would reject.
            if (m_Pinned != null)
            {
                m_Target = m_Pinned;
                return;
            }

            var stillValid = m_Target != null && IsLiveAgentCar(m_Target);
            if (stillValid && Time.unscaledTime < m_NextSearch)
            {
                return;
            }

            m_NextSearch = Time.unscaledTime + 0.25f;

            foreach (var car in FindObjectsByType<CarController>(FindObjectsSortMode.None))
            {
                if (IsLiveAgentCar(car))
                {
                    m_Target = car;
                    return;
                }
            }

            if (!stillValid)
            {
                m_Target = null;
            }
        }

        /// <summary>
        /// True for the competitor's car, and only while it is actually still racing. Four separate
        /// conditions, each ruling out one specific kind of car that is not what a spectator wants to
        /// watch:
        /// <list type="bullet">
        /// <item>parked below the world (an idle rig waiting its turn)</item>
        /// <item>anything wearing a <see cref="GhostCar"/> tag. The baseline would be excluded by the
        /// last condition anyway — it drives itself through <see cref="RacingBotCup.Agent.BaselineBot"/>
        /// directly on the car, never through a <see cref="RacerAgent"/> — but a ghost running a
        /// competitor's own past model does have one, and is otherwise indistinguishable from the
        /// car it is there to be compared against</item>
        /// <item>a car with no <see cref="RacerAgent"/> at all</item>
        /// <item>a competitor's car that already finished its seed — sequential evaluation leaves
        /// these sitting rather than destroying them, so without this a re-search can hand the camera
        /// any one of however many have piled up so far, however far away it stopped</item>
        /// </list>
        /// </summary>
        public static bool IsLiveAgentCar(CarController car)
        {
            return car.IsRacing
                && car.transform.position.y > -100f
                && car.GetComponent<GhostCar>() == null
                && car.GetComponentInChildren<RacerAgent>() != null;
        }
    }
}
