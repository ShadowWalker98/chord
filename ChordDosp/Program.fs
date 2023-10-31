open System
open Akka.FSharp
open System.Threading

/////////////////
// Hop counter //
/////////////////
type HopCounterMessage =
    | IncrementConvergedNode of int * int * int

let hopCounter numNodes (mailbox: Actor<_>) = 
    let mutable totalHopCount = 0
    let mutable totalnumRequest = 0
    let mutable totalConvergedNodes = 0
    let rec loop () = actor {
        let! message = mailbox.Receive ()
        match message with
        | IncrementConvergedNode (nodeID, hopCount, numRequest) ->
            printfn "NodeID: %d converged with hopCount: %d, numRequest: %d" nodeID hopCount numRequest
            totalHopCount <- totalHopCount + hopCount
            totalnumRequest <- totalnumRequest + numRequest
            totalConvergedNodes <- totalConvergedNodes + 1
            if(totalConvergedNodes = numNodes) then
                printfn "Total number of hops: %d" totalHopCount
                printfn "Total number of requests: %d" totalnumRequest
                printfn "Average number of hops: %f" ((float totalHopCount) / (float totalnumRequest))
                mailbox.Context.System.Terminate() |> ignore
        // Handle message here
        return! loop ()
    }
    loop ()

//////////
// Node //
//////////
type NodeMessage =
    | Create
    | Join of int
    | FindNodeSuccessor of int
    | ReceiveSuccessor of int
    | Stabilize
    | FindPredecessor
    | ReceivePredecessor of int
    | Notify of int
    | FixFingers
    | FindFingerSuccessor of int * int * int
    | UpdateFinger of int * int   
    | StartQuerying
    | QueryMessage
    | FindKeySuccessor of int * int * int
    | FoundKey of int

let getActorPath s =
    let actorPath = @"akka://my-system/user/" + string s
    actorPath

let inBetweenWithoutLeftWithoutRight hashSpace left value right =
    let correctedRight = if(right < left) then right + hashSpace else right
    let correctedValue = if((value < left) && (left > right)) then (value + hashSpace) else value
    (left = right) || ((correctedValue > left) && (correctedValue < correctedRight))

let inBetweenWithoutLeftWithRight hashSpace left value right =
    let correctedRight = if(right < left) then right + hashSpace else right
    let correctedValue = if((value < left) && (left > right)) then (value + hashSpace) else value
    (left = right) || ((correctedValue > left) && (correctedValue <= correctedRight))

let myActor (nodeID: int) m maxNumRequests hopCounterRef (mailbox: Actor<_>) =
    printfn "[INFO] Creating node %d" nodeID
    let hashSpace = int (Math.Pow(2.0, float m))
    let mutable predecessorID = -1
    let mutable fingerTable = Array.create m -1
    let mutable next = 0
    let mutable totalHopCount = 0
    let mutable numRequests = 0

    let rec loop () = actor {
        let! message = mailbox.Receive ()
        let sender = mailbox.Sender ()
        match message with
        | Create ->
            predecessorID <- -1
            for i = 0 to m - 1 do
                fingerTable.[i] <- nodeID
            mailbox.Context.System.Scheduler.ScheduleTellRepeatedly (
                TimeSpan.FromMilliseconds(0.0),
                TimeSpan.FromMilliseconds(500.0),
                mailbox.Self,
                Stabilize
            )
            mailbox.Context.System.Scheduler.ScheduleTellRepeatedly (
                    TimeSpan.FromMilliseconds(0.0),
                    TimeSpan.FromMilliseconds(500.0),
                    mailbox.Self,
                    FixFingers
                )

        | Join (nDash) ->
            predecessorID <- -1
            let nDashPath = getActorPath nDash
            let nDashRef = mailbox.Context.ActorSelection nDashPath
            nDashRef <! FindNodeSuccessor (nodeID)

        | FindNodeSuccessor (id) ->
            if(inBetweenWithoutLeftWithRight hashSpace nodeID id fingerTable.[0]) then
                let newNodePath = getActorPath id
                let newNodeRef = mailbox.Context.ActorSelection newNodePath
                newNodeRef <! ReceiveSuccessor (fingerTable.[0])
            else
                let mutable i = m - 1
                while(i >= 0) do
                    if(inBetweenWithoutLeftWithoutRight hashSpace nodeID fingerTable.[i] id) then
                        let closestPrecedingNodeID = fingerTable.[i]
                        let closestPrecedingNodePath = getActorPath closestPrecedingNodeID
                        let closestPrecedingNodeRef = mailbox.Context.ActorSelection closestPrecedingNodePath
                        closestPrecedingNodeRef <! FindNodeSuccessor (id)
                        i <- -1
                    i <- i - 1

        | ReceiveSuccessor (succesorID) ->
            for i = 0 to m - 1 do
                fingerTable.[i] <- succesorID
            // Start stabilize and fix_fingers schedulers
            mailbox.Context.System.Scheduler.ScheduleTellRepeatedly (
                TimeSpan.FromMilliseconds(0.0),
                TimeSpan.FromMilliseconds(500.0),
                mailbox.Self,
                Stabilize
            )
            mailbox.Context.System.Scheduler.ScheduleTellRepeatedly (
                    TimeSpan.FromMilliseconds(0.0),
                    TimeSpan.FromMilliseconds(500.0),
                    mailbox.Self,
                    FixFingers
                )

        | Stabilize ->
            let successorID = fingerTable.[0]
            let succesorPath = getActorPath successorID
            let succesorRef = mailbox.Context.ActorSelection succesorPath
            succesorRef <! FindPredecessor

        | FindPredecessor ->
            sender <! ReceivePredecessor (predecessorID)

        | ReceivePredecessor (x) ->
            if((x <> -1) && (inBetweenWithoutLeftWithoutRight hashSpace nodeID x fingerTable.[0])) then
                fingerTable.[0] <- x
            let successorID = fingerTable.[0]
            let succesorPath = getActorPath successorID
            let successorRef = mailbox.Context.ActorSelection succesorPath
            successorRef <! Notify (nodeID)

        | Notify (nDash) ->
            if((predecessorID = -1) || (inBetweenWithoutLeftWithoutRight hashSpace predecessorID nDash nodeID)) then
                predecessorID <- nDash

        | FixFingers ->
            next <- next + 1
            if(next >= m) then
                next <- 0
            let fingerValue = nodeID + int (Math.Pow(2.0, float (next)))
            mailbox.Self <! FindFingerSuccessor (nodeID, next, fingerValue)

        | FindFingerSuccessor (originNodeID, next, id) ->
            if(inBetweenWithoutLeftWithRight hashSpace nodeID id fingerTable.[0]) then
                let originNodePath = getActorPath originNodeID
                let originNodeRef = mailbox.Context.ActorSelection originNodePath
                originNodeRef <! UpdateFinger (next, fingerTable.[0])
            else
                let mutable i = m - 1
                while(i >= 0) do
                    if(inBetweenWithoutLeftWithoutRight hashSpace nodeID fingerTable.[i] id) then
                        let closestPrecedingNodeID = fingerTable.[i]
                        let closestPrecedingNodePath = getActorPath closestPrecedingNodeID
                        let closestPrecedingNodeRef = mailbox.Context.ActorSelection closestPrecedingNodePath
                        closestPrecedingNodeRef <! FindFingerSuccessor (originNodeID, next, id)
                        i <- -1
                    i <- i - 1

        | UpdateFinger (next, fingerSuccessor) ->
            fingerTable.[next] <- fingerSuccessor

        | StartQuerying ->
            if(numRequests < maxNumRequests) then
                mailbox.Self <! QueryMessage
                mailbox.Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.), mailbox.Self, StartQuerying)
            else
                // Done querying. Send its current status to the hop counter
                hopCounterRef <! IncrementConvergedNode (nodeID, totalHopCount, numRequests)

        | QueryMessage ->
            let key = (System.Random()).Next(hashSpace)
            mailbox.Self <! FindKeySuccessor (nodeID, key, 0)

        // // Simple key lookup
        // | FindKeySuccessor (originNodeID, id, numHops) ->
        //     if(id = nodeID) then
        //         // printfn "Key %d is at the node %d in %d hops" id nodeID numHops
        //         let originNodePath = getActorPath originNodeID
        //         let originNodeRef = mailbox.Context.ActorSelection originNodePath
        //         originNodeRef <! FoundKey (numHops)
        //     elif(inBetweenWithoutLeftWithRight hashSpace nodeID id fingerTable.[0]) then
        //         // printfn "Key %d is at the node %d in %d hops" id fingerTable.[0] numHops
        //         let originNodePath = getActorPath originNodeID
        //         let originNodeRef = mailbox.Context.ActorSelection originNodePath
        //         originNodeRef <! FoundKey (numHops)
        //     else
        //         let successorID = fingerTable.[0]
        //         let successorPath = getActorPath successorID
        //         let successorRef = mailbox.Context.ActorSelection successorPath
        //         successorRef <! FindKeySuccessor (originNodeID, id, numHops + 1)

        // Scalable key lookup
        | FindKeySuccessor (originNodeID, id, numHops) ->
            if(id = nodeID) then
                // printfn "Key %d is at the node %d in %d hops" id nodeID numHops
                let originNodePath = getActorPath originNodeID
                let originNodeRef = mailbox.Context.ActorSelection originNodePath
                originNodeRef <! FoundKey (numHops)
            elif(inBetweenWithoutLeftWithRight hashSpace nodeID id fingerTable.[0]) then
                // printfn "Key %d is at the node %d in %d hops" id fingerTable.[0] numHops
                let originNodePath = getActorPath originNodeID
                let originNodeRef = mailbox.Context.ActorSelection originNodePath
                originNodeRef <! FoundKey (numHops)
            else
                let mutable i = m - 1
                while(i >= 0) do
                    if(inBetweenWithoutLeftWithoutRight hashSpace nodeID fingerTable.[i] id) then
                        let closestPrecedingNodeID = fingerTable.[i]
                        let closestPrecedingNodePath = getActorPath closestPrecedingNodeID
                        let closestPrecedingNodeRef = mailbox.Context.ActorSelection closestPrecedingNodePath
                        closestPrecedingNodeRef <! FindKeySuccessor (originNodeID, id, numHops + 1)
                        i <- -1
                    i <- i - 1

        | FoundKey (hopCount) ->
            if(numRequests < maxNumRequests) then
                totalHopCount <- totalHopCount + hopCount
                numRequests <- numRequests + 1

        return! loop ()
    }
    loop ()

[<EntryPoint>]
let main argv =
    // Create system
    let system = System.create "my-system" (Configuration.load())

    // Parse command line arguments
    let numNodes = int argv.[0]
    let numRequests = int argv.[1]

    // m-bit identifier
    let m = 20
    let hashSpace = int (Math.Pow(2.0, float m))

    // Spawn hopCounter
    let hopCounterRef = spawn system "hopCounter" (hopCounter numNodes)

    // // Test 1: Known nodes
    // // Spawn nodes
    // let nodeIDs = [1; 8; 14; 21; 32; 38; 42; 48; 51; 56]
    // let mutable nodeRefs = Array.create nodeIDs.Length null
    // for i = 0 to nodeIDs.Length - 1 do
    //     nodeRefs.[i] <- spawn system (string nodeIDs.[i]) (myActor nodeIDs.[i] m numRequests hopCounterRef)
    //     if(i = 0) then
    //         nodeRefs.[i] <! Create
    //     else
    //         nodeRefs.[i] <! Join(1)
    //     Thread.Sleep(100)
    // // Wait for some time to get system stabilized
    // printfn "Waiting for 30 sec to get system stabilized"
    // Thread.Sleep(30000)
    // // Start querying
    // nodeRefs.[1] <! FindKeySuccessor (nodeIDs.[1], 54, 0);
    // // // Start querying
    // // for nodeRef in nodeRefs do
    // //     nodeRef <! StartQuerying
    // //     Thread.Sleep(100)

    // Test 2: Unknown nodes
    // Spawn nodes
    let nodeIDs = Array.create numNodes -1;
    let nodeRefs = Array.create numNodes null;
    let mutable i = 0;
    while(i < numNodes) do
        try
            let nodeID  = (Random()).Next(hashSpace)
            nodeIDs.[i] <- nodeID
            nodeRefs.[i] <- spawn system (string nodeID) (myActor nodeID m numRequests hopCounterRef)
            if(i = 0) then
                nodeRefs.[i] <! Create
            else
                nodeRefs.[i] <! Join(nodeIDs.[0])
            i <- i + 1
            Thread.Sleep(500)
        with _ -> ()
    // Wait for some time to get system stabilized
    printfn "Waiting for 30 sec to get system stabilized"
    Thread.Sleep(30000)
    // Start querying
    for nodeRef in nodeRefs do
        nodeRef <! StartQuerying
        Thread.Sleep(500)

    // Wait till all the actors are terminated
    system.WhenTerminated.Wait()

    0 // return an integer exit code