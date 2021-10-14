using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;

public class HummingbirdAgent : Agent
{

    [Tooltip("Force to apply when moving")]
    public float moveForce = 2f;

    [Tooltip("Speed to pitch up or down")]
    public float pitchSpeed = 100f;

    [Tooltip("Speed to rotate around the up axis")]
    public float yawSpeed = 100f;

    [Tooltip("Transform at the tip of the beak")]
    public Transform beakTip;

    [Tooltip("The agent's camera")]
    public Camera agentCamera;

    [Tooltip("Whether this is training mode or gameplay mode")]
    public bool trainingMode;

    //Rigidbody of agent
    new private Rigidbody rigidbody;

    //Flower area agent is in
    private FlowerArea flowerArea;

    //Nearest flower to agent
    private Flower nearestFlower;

    //Allows for smoother pitch changes
    private float smoothPitchChange = 0f;

    //Allows for smoother yaw changes
    private float smoothYawChange = 0f;

    //Maximum angle that the bird can pitch up or down
    private const float MaxPitchAngle = 80f;

    //Maximum distance from the beak tip to accept nectar collision
    private const float BeakTipRadius = 0.008f;

    //Whether the agent is frozen (intentionally not flying)
    private bool frozen = false;

    /// <summary>
    /// The amount of nectar the agent has obtained this episode
    /// </summary>
    public float NectarObtained { get; private set; }


    public override void Initialize()
    {
        rigidbody = GetComponent<Rigidbody>();
        flowerArea = GetComponentInParent<FlowerArea>();
        if (!trainingMode) MaxStep = 0;
    }

    /// <summary>
    /// Resets enviroinment when training begins
    /// </summary>
    public override void OnEpisodeBegin()
    {
        if (trainingMode)
        {
            //Only reset flowers in training when there is one agent per area
            flowerArea.ResetFlowers();
        }

        //Reset nectar obtained
        NectarObtained = 0f;

        //Zero out felocities so that movement stops before a new episode begins
        rigidbody.velocity = Vector3.zero;
        rigidbody.angularVelocity = Vector3.zero;

        //Default to spawning in front of a flower
        bool inFrontOfFlower = true;
        if (trainingMode)
        {
            //spawn in front of lower 50% of the time during training
            inFrontOfFlower = UnityEngine.Random.value > .5f;
        }

        //Move the agent to a new random position
        MoveToSafeRandomPosition(inFrontOfFlower);

        //Recalculate the nearest flower now that the agent has moved
        UpdateNearestFlower();
    }

    /// <summary>
    /// Called when an action is received from either the play input of the neural network
    ///
    /// vectorAction[i] represents:
    /// Index 0: move vector X (+1 = right, -1 = left)
    /// Index 1: move vector Y (+1 = up, -1 = down)
    /// Index 2: move vector Z (+1 = forward, -1 = backward)
    /// Index 3: pitch angle (+1 = pitch up, -1 = pitch down)
    /// Index 4: yaw angle (+1 = turn right, -1 = turn left)
    /// 
    /// </summary>
    /// <param name="vectorAction">The actions to take</param>
    public override void OnActionReceived(float[] vectorAction)
    {
        //Don't take actions if frozen
        if (frozen) return;

        //Calculate movement vector
        Vector3 move = new Vector3(vectorAction[0], vectorAction[1], vectorAction[2]);

        //add force in the direction of the move vector
        rigidbody.AddForce(move * moveForce);

        //get the current rotation
        Vector3 rotationVector = transform.rotation.eulerAngles;

        //Calculate pitch and yaw rotation
        float pitchChange = vectorAction[3];
        float yawChange = vectorAction[4];

        //Calculate smooth rotation changes
        smoothPitchChange = Mathf.MoveTowards(smoothPitchChange, pitchChange, 2f * Time.fixedDeltaTime);
        smoothYawChange = Mathf.MoveTowards(smoothYawChange, yawChange, 2f * Time.fixedDeltaTime);

        //Calculate new pitch and yaw based on smooth values
        //Clamp pitch to avoid flipping upside down
        float pitch = rotationVector.x + smoothPitchChange * Time.fixedDeltaTime * pitchSpeed;
        if (pitch > 180f) pitch -= 360f;
        pitch = Mathf.Clamp(pitch, -MaxPitchAngle, MaxPitchAngle);

        float yaw = rotationVector.y + smoothYawChange * Time.fixedDeltaTime * yawSpeed;

        //Apply the new rotation
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    /// <summary>
    /// Collect vector observations from the environment
    /// </summary>
    /// <param name="sensor"></param>
    public override void CollectObservations(VectorSensor sensor)
    {
        //Account for if no flower
        if (nearestFlower == null)
        {
            sensor.AddObservation(new float[0]);
            return;
        }

        //Observe the agent's local rotation (4 observations)
        sensor.AddObservation(transform.localRotation.normalized);

        //Get a vector from the beak tip to the nearest flower
        Vector3 toFlower = nearestFlower.FlowerCenterPosition - beakTip.position;

        //Observe a normalized vector pointing to the nearest flower (3 observations)
        sensor.AddObservation(toFlower.normalized);

        //Observe a dot product that indicates whether the beak tip is in front of the flower (1 observation)
        // (+1 means that the beaktip is directly in front of the flower, -1 means directly behind)
        sensor.AddObservation(Vector3.Dot(toFlower.normalized, -nearestFlower.FlowerUpVector.normalized));

        //Observe a dot product that indicates whether the beak is pointing toward the flower (1 observation)
        //(+1 means the beak is pointing directly at the flower, -1 means directly away)
        sensor.AddObservation(Vector3.Dot(beakTip.forward.normalized, -nearestFlower.FlowerUpVector.normalized));

        //Observe the relative distance from the beak tip to the flower (1 observation)
        sensor.AddObservation(toFlower.magnitude / FlowerArea.AreaDiameter);
    }


    /// <summary>
    /// When behavior type is set to "heuristic Only" on the agen'ts Behavior paramters,
    /// this function will be called. Its return values will be fed into
    /// <see cref="OnActionReceived(float[])"/> instead of using the neural network
    /// </summary>
    /// <param name="actionsOut"></param>
    public override void Heuristic(float[] actionsOut)
    {
        //Create placeholders for all movement/turning
        Vector3 forward = Vector3.zero;
        Vector3 left = Vector3.zero;
        Vector3 up = Vector3.zero;
        float pitch = 0f;
        float yaw = 0f;

        //Convert keyboard inputs to movement and turning
        //All values should be between -1 and +1

        //Forward/Backward
        if (Input.GetKey(KeyCode.W)) forward = transform.forward;
        else if (Input.GetKey(KeyCode.S)) forward = -transform.forward;

        //Left/right
        if (Input.GetKey(KeyCode.A)) left = -transform.right;
        else if (Input.GetKey(KeyCode.D)) left = transform.right;

        //Up/Down
        if (Input.GetKey(KeyCode.E)) up = transform.up;
        else if (Input.GetKey(KeyCode.C)) up = -transform.up;

        //pitch up/down
        if (Input.GetKey(KeyCode.UpArrow)) pitch = 1f;
        else if (Input.GetKey(KeyCode.DownArrow)) pitch = -1f;

        //yaw up/down
        if (Input.GetKey(KeyCode.LeftArrow)) yaw = 1f;
        else if (Input.GetKey(KeyCode.RightArrow)) yaw = -1f;

        //combine the movement vectors and normalize
        Vector3 combined = (forward + left + up).normalized;

        //Add the 3 movement values, pitch, and yaw to the actionsOut array
        actionsOut[0] = combined.x;
        actionsOut[1] = combined.y;
        actionsOut[2] = combined.z;
        actionsOut[3] = pitch;
        actionsOut[4] = yaw;
    }

    /// <summary>
    /// Prevent the agent from moving and taking actions
    /// </summary>
    public void FreezeAgent()
    {
        Debug.Assert(trainingMode == false, "Freeze/Unfreeze not supported in training");
        frozen = true;
        rigidbody.Sleep();
    }

    /// <summary>
    /// Resume agent movement and actions
    /// </summary>
    public void UnfreezeAgent()
    {
        Debug.Assert(trainingMode == false, "Freeze/Unfreeze not supported in training");
        frozen = false;
        rigidbody.WakeUp();
    }


    /// <summary>
    /// Move the agent to a safe random position
    /// </summary>
    /// <param name="inFrontOfFlower"></param>
    private void MoveToSafeRandomPosition(bool inFrontOfFlower)
    {
        bool safePositionFound = false;
        int attemptsRemaining = 100; //Prevent an infinite loop if there is no safe position
        Vector3 potentialPosition = Vector3.zero;
        Quaternion potentialRotation = new Quaternion();

        //Loop until a safe position is found or we run out of attempts
        while (!safePositionFound && attemptsRemaining > 0) {
            attemptsRemaining--;
            if(inFrontOfFlower)
            {
                //Pick a random flower
                Flower randomFlower = flowerArea.Flowers[UnityEngine.Random.Range(0, flowerArea.Flowers.Count)];

                //Position 10-20cm in front of the flower
                float distanceFromFlower = UnityEngine.Random.Range(.1f, .2f);
                potentialPosition = randomFlower.transform.position + randomFlower.FlowerUpVector * distanceFromFlower;

                //Point beak at flower (bird's head is center of transform)
                Vector3 toFlower = randomFlower.FlowerCenterPosition - potentialPosition;
                potentialRotation = Quaternion.LookRotation(toFlower, Vector3.up);
            }
            else
            {
                //pick a random height from the ground
                float height = UnityEngine.Random.Range(1.2f, 2.5f);

                //pick a random radius from the center of the area
                float radius = UnityEngine.Random.Range(2f, 7f);

                //Pick a random direction rotated around the y-axis
                Quaternion direction = Quaternion.Euler(0f, UnityEngine.Random.Range(-180f, 180f), 0f);

                //Combine hight, radius, direction
                potentialPosition = flowerArea.transform.position + Vector3.up * height + direction * Vector3.forward * radius;

                //choose and set random starting pitch/yaw
                float pitch = UnityEngine.Random.Range(-60f, 60f);
                float yaw = UnityEngine.Random.Range(-180f, 180f);
                potentialRotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            Collider[] colliders = Physics.OverlapSphere(potentialPosition, 0.05f);

            //Safe position has been found if no colliders are overlapped
            safePositionFound = colliders.Length == 0;
        }

        Debug.Assert(safePositionFound, "Could not find a safe position to spawn");
        transform.position = potentialPosition;
        transform.rotation = potentialRotation;
    }

    /// <summary>
    /// Update the nearest flower to the agents
    /// </summary>
    private void UpdateNearestFlower()
    {
        foreach(Flower flower in flowerArea.Flowers)
        {
            if (nearestFlower == null && flower.HasNectar)
            {
                //no current nearest flower and this flower has nectar, so set to this flower
                nearestFlower = flower;
            }
            else if (flower.HasNectar)
            {
                //calculate distance to this flower and distance to the current nearest flower
                float distanceToFlower = Vector3.Distance(flower.transform.position, beakTip.position);
                float distanceToCurrentNearestFlower = Vector3.Distance(nearestFlower.transform.position, beakTip.position);

                //if current nearest flower is empty OR this flower is closer, update the nearest flower
                if (!nearestFlower.HasNectar || distanceToFlower < distanceToCurrentNearestFlower)
                {
                    nearestFlower = flower;
                }
            }
        }
    }

    /// <summary>
    /// Called when the agent's collider enters a trigger collider
    /// </summary>
    /// <param name="other">The trigger collider</param>
    private void OnTriggerEnter(Collider other)
    {
        TriggerEnterOrStay(other);
    }

    /// <summary>
    /// Called when the agent's collider enters a trigger collider
    /// </summary>
    /// <param name="other">The trigger collider</param>
    private void OnTriggerStay(Collider other)
    {
        TriggerEnterOrStay(other);
    }

    /// <summary>
    /// Handles when the agent's collider enters or stays in a trigger collider
    /// </summary>
    /// <param name="collider">The trigger collider</param>
    private void TriggerEnterOrStay(Collider collider)
    {
        //Check if agent is colliding with nectar
        if (collider.CompareTag("nectar"))
        {
            Vector3 closestPointToBeakTip = collider.ClosestPoint(beakTip.position);

            //check if the closest collision point is close to the beka tip
            //Note: a collision with anything but the beak tip should not count
            if (Vector3.Distance(beakTip.position, closestPointToBeakTip) < BeakTipRadius)
            {
                //Look up the flower for this nectar colliders
                Flower flower = flowerArea.GetFlowerFromNectar(collider);

                //Attempt to take .01 nectar
                //Note: this is per fixed timestep, meaning it happens every .02 seconds, or 50x per second
                float nectarReceived = flower.Feed(.01f);

                //Keep track of nectar obtained
                NectarObtained += nectarReceived;

                if (trainingMode)
                {
                    //Calculate reward for getting nectar
                    float bonus = .02f * Mathf.Clamp01(Vector3.Dot(transform.forward.normalized, -nearestFlower.FlowerUpVector.normalized));
                    AddReward(.01f + bonus);
                }

                if (!flower.HasNectar)
                {
                    UpdateNearestFlower();
                }
            }
        }
    }

    /// <summary>
    /// Called when the agent collides with something solid
    /// </summary>
    /// <param name="collision">The collision info</param>
    private void OnCollisionEnter(Collision collision)
    {
        if (trainingMode && collision.collider.CompareTag("boundary"))
        {
            //Collided with the area boundary, give a negative reward
            AddReward(-.5f);
        }   
    }


    /// <summary>
    /// Called every frame
    /// </summary>
    private void Update()
    {
        //Draw a line from beaktip to nearest flower
        if(nearestFlower != null)
        {
            Debug.DrawLine(beakTip.position, nearestFlower.FlowerCenterPosition, Color.green);
        }
    }


    /// <summary>
    /// Called every .02 seconds
    /// </summary>
    private void FixedUpdate()
    {
        //Avoids scenario where nearest flower nectar is stolen by opponent and not updated
        if (nearestFlower != null && !nearestFlower.HasNectar)
        {
            UpdateNearestFlower();
        }
    }
}
