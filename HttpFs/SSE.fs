namespace HttpFs

/// See https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation
module SSE =
  open Hopac
  open Hopac.Infixes

  let (|EventComplete|Comment|Field|) (line: string) =
    if line = "" then EventComplete else
    match line.IndexOf ":" with
    | 0 ->
      Comment (line.Substring 1)
    | n when n > 0 ->
      let before = line.Substring(0,n)
      let after = line.Substring(n+1)
      let fieldValue =
        if after.StartsWith " " then
          after.Substring 1
        else
          after
      Field (before, fieldValue)
    | _ ->
      Field (line, "")

  let isASCIIDigit c =
    System.Char.IsDigit c

  let (|EventType|Data|EventId|Retry|Ignore|) (fieldName, fieldValue: string) =
    match fieldName with
    | "event" ->
      EventType fieldValue

    | "data" ->
      Data fieldValue

    | "id" ->
      match fieldValue.IndexOf "\u0000" with
      | n when n > 0 ->
        Ignore
      | _ ->
        EventId fieldValue

    | "retry" ->
      if fieldValue.ToCharArray() |> Array.forall isASCIIDigit then
          Retry (int fieldValue)
      else
        Ignore

    | _ ->
      Ignore
  
  type State = {
    eventType: string
    data: string list // stored in reverse order
    lastEventId: string
    reconnectionTime: int
  }

  let initialState = {
    eventType = ""
    data = []
    lastEventId = ""
    reconnectionTime = 0
  }

  let interpretField state =
    function
    | EventType et ->
      { state with eventType = et }

    | Data d ->
      { state with data = d::state.data }

    | EventId eid ->
      { state with lastEventId = eid }

    | Retry r ->
      { state with reconnectionTime = r }

    | Ignore ->
      state

  let reset (state: State) = {
    eventType = ""
    data = []
    lastEventId = state.lastEventId
    reconnectionTime = state.reconnectionTime
  }

  type Event = {
    eventType: string
    data: string
    lastEventId: string
  }

  let dispatchEvent (state: State) =
    let data =
      state.data
      |> List.rev
      |> String.concat "\n"

    if data = "" then
      None
    else
      let data =
        if data.EndsWith "\u+000A" then data.Substring (0, data.Length - 1) else data
      let eventType =
        if state.eventType = "" then "message" else state.eventType
      Some
        { eventType = eventType
          data = data
          lastEventId = state.lastEventId }

  let interpret (state: State) =
    function
    | EventComplete ->
      reset state, dispatchEvent state

    | Comment _ ->
      state, None

    | Field (n,v) ->
      interpretField state (n,v), None

  let events lines =
    let init =
      initialState, None
    Seq.scan (fun (s, _) l -> interpret s l) init lines
    |> Seq.choose snd

  /// Stateful streaming
  type Streamer = {
    read: Job<Streamer * Event>
  }

  let ensureString =
    function
    | null -> ""
    | s -> s

  let streamEvents streamer =
    let rec inner s =
      streamer
      // >>- (fun msg -> printfn "In: %s" msg; msg)
      >>- (ensureString >> interpret s)
      >>= function (s, eo) ->
            match eo with
            | Some e ->
              // printfn "Out: %A" e
              Job.result ( { read = inner s }, e)
            | None ->
              inner s
    { read = inner initialState }
