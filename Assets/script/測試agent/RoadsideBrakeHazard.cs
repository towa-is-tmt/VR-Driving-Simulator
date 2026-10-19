using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class RoadsideBrakeHazard : Agent
{
    public enum HazardState { Parked, PullingOut, Braking, Done }
    public HazardState currentState = HazardState.Parked;
    public Transform vrCar;
    private Rigidbody rb;
    private float stateTimer = 0f;
    public float pullOutDuration = 1.5f;

    public override void Initialize() { rb = GetComponent<Rigidbody>(); }

    public override void OnEpisodeBegin()
    {
        currentState = HazardState.Parked;
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        stateTimer = 0f;
        if (vrCar != null) vrCar.GetComponent<TargetCarController>().ResetPosition();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.InverseTransformDirection(rb.velocity).z);
        sensor.AddObservation(vrCar != null ? transform.InverseTransformPoint(vrCar.position) : Vector3.zero);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (vrCar == null || currentState == HazardState.Parked) return;

        float forwardAction = actions.ContinuousActions[0];
        float steerAction = actions.ContinuousActions[1];

        // 簡單物理移動控制
        rb.AddForce(transform.forward * forwardAction * 20f, ForceMode.Acceleration);
        rb.AddTorque(transform.up * steerAction * 15f, ForceMode.Acceleration);

        if (currentState == HazardState.PullingOut)
        {
            stateTimer += Time.fixedDeltaTime;
            if (stateTimer >= pullOutDuration) currentState = HazardState.Braking;
        }
        else if (currentState == HazardState.Braking)
        {
            Vector3 relativePlayerPos = transform.InverseTransformPoint(vrCar.position);
            if (relativePlayerPos.z < 0 && relativePlayerPos.z > -15f) AddReward(0.01f);
            if (relativePlayerPos.z > 5.0f) { AddReward(-1.0f); EndEpisode(); }
        }
    }

    void FixedUpdate()
    {
        if (vrCar != null && currentState == HazardState.Parked)
        {
            Vector3 relativePos = transform.InverseTransformPoint(vrCar.position);
            if (relativePos.z < 0 && relativePos.z > -30f) currentState = HazardState.PullingOut;
        }
    }
}