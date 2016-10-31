#load "./paket-files/include-scripts/net46/include.main.group.fsx"

[<AutoOpen>]
module AsyncExt = 
    type AsyncBuilder with

        member __.Bind(t : System.Threading.Tasks.Task<'t>, u) = async.Bind(Async.AwaitTask(t), u)

[<RequireQualifiedAccess>]
module Counter =
    type Agent<'t> = MailboxProcessor<'t>
    type Reply<'t> = AsyncReplyChannel<'t>
    type Action = 
    /// Get the current value
    | Get of Reply<int>
    /// Increment the internal counter
    | Increment
    /// Reset the counter
    | Reset

    let agent max = 
        let rec loop count = 
            fun (inbox : Agent<Action>) ->
                async {
                    let! cmd = inbox.Receive()
                    match cmd with
                    | Get replyCh -> 
                        printfn "echoing count of %d" count
                        replyCh.Reply(count)
                        return! loop count inbox
                    | Increment -> 
                        if count < max 
                        then 
                            printfn "incrementing count to %d" (count + 1) 
                            return! loop (count + 1) inbox
                        else 
                            printfn "keeping count at %d" count
                            return! loop count inbox
                    | Reset ->
                        printfn "resetting count"
                        return! loop 0 inbox
                }
        Agent.Start <| loop 0

module Github = 
    open System
    open System.Threading
    open Octokit
    open System.Collections.Concurrent
    
    type ApiResult<'t> = 
        /// common case, we got our data back
        | Ok of data : 't
        /// Rate-limit hit, can't access anymore until the given time
        | Limited of until : TimeSpan
    
    type ApiToken = string
    type GitHubCounter = Counter.Agent<Counter.Action>
    let counters = new ConcurrentDictionary<ApiToken, GitHubCounter>()
    
    /// Create a new counter. You should make one of these for each API Token that is used, because rates are per-token
    let githubRateCounter () = 
        let counter = Counter.agent 20
        let minute = 60. * 1000.
        let t = new Timer((fun _ -> counter.Post(Counter.Reset)), (), TimeSpan.FromSeconds(0.), TimeSpan.FromMinutes(1.))
        counter

    let timeUntilUnblocked (client : IGitHubClient) = 
        let info = client.GetLastApiInfo()
        let limit = info.RateLimit
        limit.Reset - DateTimeOffset.UtcNow
    
    let ms (span :TimeSpan) = span.TotalMilliseconds

    let sleepUntilOk client = 
        let time = timeUntilUnblocked client
        printfn "sleeping for %f seconds" time.TotalSeconds
        Async.Sleep(int time.TotalSeconds)

    let rec performCall (client : GitHubClient) fn = async {
        let counter = counters.GetOrAdd(client.Credentials.GetToken(), fun _ -> githubRateCounter())    
        let! count = counter.PostAndAsyncReply(fun reply -> Counter.Get reply)
        match count with
        | i when i < 20 -> 
            // reserve our slot
            counter.Post(Counter.Increment)
            // do the call, taking care to catch potential exceptional rate limits
            let! result = Async.Catch(fn client)
            match result with
            | Choice1Of2 data -> return data
            | Choice2Of2 ex ->
                do! sleepUntilOk client
                return! performCall client fn
        | _ -> 
            printfn "too many calls this minute, trying again in a moment"
            do! sleepUntilOk client
            return! performCall client fn
    }
    
module Test =
    open Github
    let testThrottling () = 
        async {
            use c = githubRateCounter()
            for i in 1..70 do
                c.Post(Counter.Increment)
                do! Async.Sleep(1000) 
            let! response = c.PostAndAsyncReply(fun ch -> Counter.Get ch)
            printfn "%d" response
        } |> Async.RunSynchronously