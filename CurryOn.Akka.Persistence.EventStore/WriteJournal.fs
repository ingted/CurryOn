﻿namespace Akka.Persistence.EventStore

open Akka.Actor
open Akka.Configuration
open Akka.Persistence
open Akka.Persistence.EventStore
open Akka.Persistence.Journal
open Akka.Streams
open Akka.Streams.Dsl
open CurryOn.Akka
open CurryOn.Akka.Serialization
open CurryOn.Common
open FSharp.Control
open EventStore.ClientAPI
open Microsoft.VisualStudio.Threading
open System
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks
open Akka.Persistence.Query

module EventJournal =
    let get (config: Config) (context: IActorContext) =
        let plugin = EventStorePlugin(context)
        let connect () = plugin.Connect () 
        let readBatchSize = config.GetInt("read-batch-size", 4095)
        let getMetadataStream persistenceId = sprintf "snapshots-%s" persistenceId
        let getStream persistenceId version = sprintf "snapshot-%s-%d" persistenceId version
        let serializeSnapshotMetadata (snapshot: JournalSnapshot) =
            let metadata = {PersistenceId = snapshot.PersistenceId; SequenceNumber = snapshot.SequenceNumber; Timestamp = snapshot.Timestamp}
            let bytes = metadata |> toJsonBytes
            EventData(Guid.NewGuid(), typeof<SnapshotMetadata>.Name, true, bytes, [||])
        let deserializeSnapshotMetadata (resolvedEvent: ResolvedEvent) = 
            resolvedEvent.Event.Data |> parseJsonBytes<SnapshotMetadata>
        let serializeEvent (event: JournaledEvent) = 
            let eventMetadata = {EventType = event.Manifest; Sender = event.Sender; Tags = event.Tags}
            EventData(Guid.NewGuid(), event.Manifest, true, event.Event |> box |> toJsonBytes, eventMetadata |> toJsonBytes)
        let deserializeEvent (resolvedEvent: ResolvedEvent) =
            let metadata = resolvedEvent.Event.Metadata |> parseJsonBytes<EventMetadata>
            let event = resolvedEvent.Event.Data |> parseJsonBytes<obj>
            (event, metadata)

        let rehydrateEvent persistenceId eventNumber (metadata: EventMetadata) (event: obj) =
            let persistent = Persistent(event, eventNumber + 1L, persistenceId, metadata.EventType, false, metadata.Sender)
            persistent :> IPersistentRepresentation

        let toPersistentRepresentation (resolvedEvent: ResolvedEvent) =
            let event, metadata = deserializeEvent resolvedEvent
            rehydrateEvent resolvedEvent.Event.EventStreamId resolvedEvent.Event.EventNumber metadata event

        let addSnapshotToMetadataLog (snapshot: JournalSnapshot) =
            operation {
                let! eventStore = connect()
                let logStream = getMetadataStream snapshot.PersistenceId
                let snapshotMetadata = snapshot |> serializeSnapshotMetadata
                let! writeResult = eventStore.AppendToStreamAsync(logStream, ExpectedVersion.Any |> int64, plugin.Credentials, snapshotMetadata)
                return snapshotMetadata.Data
            }

        let writeSnapshot (snapshot: JournalSnapshot) =
            operation {
                let! eventStore = connect()
                let! eventMetadata = addSnapshotToMetadataLog snapshot
                let eventData = EventData(Guid.NewGuid(), snapshot.Manifest, true, snapshot.Snapshot |> box |> toJsonBytes, eventMetadata) 
                return! eventStore.AppendToStreamAsync(getStream snapshot.PersistenceId snapshot.SequenceNumber, ExpectedVersion.Any |> int64, plugin.Credentials, eventData)
            }

        let rec findSnapshotMetadata (criteria: SnapshotSelectionCriteria) persistenceId startIndex =
            operation {
                let! eventStore = connect()
                let! eventSlice = eventStore.ReadStreamEventsBackwardAsync(getMetadataStream persistenceId, startIndex, readBatchSize, true, userCredentials = plugin.Credentials)
                let metadataFound = 
                    eventSlice.Events
                    |> Seq.map deserializeSnapshotMetadata
                    |> Seq.tryFind (fun metadata -> metadata.SequenceNumber <= criteria.MaxSequenceNr && metadata.Timestamp <= criteria.MaxTimeStamp)
                match metadataFound with
                | Some metadata -> return metadata |> Some
                | None -> 
                    let lastEvent = if eventSlice.Events |> isNull || eventSlice.Events.Length = 0
                                    then -1L
                                    else (eventSlice.Events |> Seq.last |> fun e -> e.Event.EventNumber)
                    if lastEvent <= 0L
                    then return None
                    else return! findSnapshotMetadata criteria persistenceId lastEvent
            }

        let findSnapshot criteria persistenceId =
            operation {
                let! eventStore = connect()
                let! snapshotMetadata = findSnapshotMetadata criteria persistenceId -1L (*end*)
                match snapshotMetadata with
                | Some metadata ->
                    let snapshotStream = getStream metadata.PersistenceId metadata.SequenceNumber
                    let! snapshotReadResult = eventStore.ReadEventAsync(snapshotStream, StreamPosition.End |> int64, true, userCredentials = plugin.Credentials) 
                    let snapshotEvent = snapshotReadResult.Event.Value
                    return (metadata, snapshotEvent.Event.Data |> parseJsonBytes<obj>) |> Some
                | None -> return None            
            }

        {new  IEventJournal with
            member __.GetCurrentPersistenceIds () =
                operation {
                    let rec readSlice startPosition ids =
                        task {
                            let! eventStore = connect()
                            let! eventSlice = eventStore.ReadStreamEventsForwardAsync("$streams", startPosition, readBatchSize, true, userCredentials = plugin.Credentials)
                            let newIds = 
                                eventSlice.Events 
                                |> Seq.filter (fun resolved -> resolved.Event |> isNotNull)
                                |> Seq.map (fun resolved -> resolved.Event.EventStreamId) |> Seq.fold (fun acc cur -> acc |> Set.add cur) ids
                            if eventSlice.IsEndOfStream |> not
                            then return! newIds |> readSlice eventSlice.NextEventNumber
                            else return newIds
                        }
                    let! persistenceIds = Set.empty<string> |> readSlice 0L
                    return! Result.success persistenceIds
                }
            member __.PersistEvents eventsToPersist =
                operation {
                    let! eventStore = connect()
                    let! result =
                        eventsToPersist 
                        |> Seq.groupBy (fun event -> event.PersistenceId)
                        |> Seq.map (fun (persistenceId, events) ->                        
                            let expectedVersion =
                                let sequenceNumber = 
                                    if events |> Seq.isEmpty
                                    then 0L
                                    else events |> Seq.map (fun event -> event.SequenceNumber) |> Seq.min
                                if sequenceNumber <= 1L
                                then ExpectedVersion.NoStream |> int64
                                else sequenceNumber - 2L
                        
                            let eventSet = events |> Seq.map serializeEvent |> Seq.toArray

                            eventStore.AppendToStreamAsync(persistenceId, expectedVersion, plugin.Credentials, eventSet))                           
                        |> Task.Parallel

                    return! Result.successWithEvents () [PersistedSuccessfully]
                }
            member __.DeleteEvents persistenceId upperLimit =
                operation {
                    let! eventStore = connect()
                    let! metadataResult = eventStore.GetStreamMetadataAsync(persistenceId, plugin.Credentials)
                    let metadata = metadataResult.StreamMetadata
                    let newMetadata = StreamMetadata.Create(metadata.MaxCount, metadata.MaxAge, upperLimit |> Nullable, metadata.CacheControl, metadata.Acl)
                    let! result = eventStore.SetStreamMetadataAsync(persistenceId, metadataResult.MetastreamVersion, newMetadata, plugin.Credentials)
                    return! Result.successWithEvents () [DeletedSuccessfully]
                }
            member __.GetMaxSequenceNumber persistenceId from =
                operation {
                    let! eventStore = connect()
                    let! eventResult = eventStore.ReadEventAsync(persistenceId, StreamPosition.End |> int64, true, plugin.Credentials)
                    match eventResult.Status with
                    | EventReadStatus.Success -> 
                        return! if eventResult.Event.HasValue
                                then if eventResult.Event.Value.Event |> isNotNull
                                        then eventResult.Event.Value.Event.EventNumber
                                        else eventResult.Event.Value.OriginalEventNumber
                                else eventResult.EventNumber
                                + 1L |> Some |> Result.success
                    | EventReadStatus.NotFound ->
                        let! streamMetadata = eventStore.GetStreamMetadataAsync(persistenceId, plugin.Credentials) 
                        return! streamMetadata.StreamMetadata.TruncateBefore.GetValueOrDefault() |> Some |> Result.success
                    | _ -> return! Result.success None                
                }
            member __.GetEvents persistenceId first last max =
                operation {
                    let! eventStore = connect()
                    let stopped = AsyncManualResetEvent(initialState = false)
                    let start = Math.Max(0L, first - 2L)
                    let eventsToRead = Math.Min(last - start + 1L, max)

                    let rec getEvents offset eventsSoFar =
                        task {
                            let! slice = eventStore.ReadStreamEventsForwardAsync(persistenceId, start, eventsToRead |> int, true, plugin.Credentials)
                            let events = slice.Events |> Seq.map toPersistentRepresentation |> Seq.toList |> List.fold (fun acc cur -> cur::acc) eventsSoFar
                        
                            if slice.IsEndOfStream
                            then return events
                            else return! getEvents slice.NextEventNumber events
                        }

                    let! events = getEvents start []
                    return! Result.success (events |> List.rev |> Seq.ofList)
                }
            member __.GetTaggedEvents tag lowOffset highOffset =
                operation {
                    let! eventStore = plugin.Connect()
                    let position = 
                        match lowOffset with
                        | Some offset -> if offset = 0L then Position.Start else Position(offset, offset)
                        | None -> Position.Start

                    let rec readSlice startPosition eventSoFar =
                        task {
                            let! eventSlice = eventStore.ReadAllEventsForwardAsync(startPosition, readBatchSize, true, userCredentials = plugin.Credentials)
                            let events = 
                                eventSlice.Events 
                                |> Seq.filter (fun resolvedEvent -> resolvedEvent.Event |> isNotNull)
                                |> Seq.map (fun resolvedEvent -> resolvedEvent, deserializeEvent resolvedEvent)
                                |> Seq.filter (fun (_,(_,metadata)) -> metadata.Tags |> Seq.contains tag)
                                |> Seq.map (fun (resolved,(event,metadata)) -> {Id = 0L; Tag = tag; Event = event |> rehydrateEvent resolved.Event.EventStreamId resolved.Event.EventNumber metadata})
                                |> Seq.fold (fun acc cur -> seq { yield! acc; yield cur }) eventSoFar
                            if eventSlice.IsEndOfStream |> not
                            then return! readSlice eventSlice.NextPosition events
                            else return events
                        }

                    let! events = readSlice Position.Start Seq.empty
                    return! Result.success events
                }
            member __.SaveSnapshot snapshot =
                operation {            
                    let! result = writeSnapshot snapshot
                    return! Result.successWithEvents () [PersistedSuccessfully]
                }
            member __.GetSnapshot persistenceId criteria =
                operation {
                    try
                        let! metadataResult = findSnapshot criteria persistenceId
                        return!
                            match metadataResult with
                            | Some (metadata, snapshot) ->
                                 { PersistenceId = persistenceId; 
                                   Manifest = snapshot.GetType().FullName; 
                                   SequenceNumber = metadata.SequenceNumber; 
                                   Timestamp = metadata.Timestamp; 
                                   Snapshot = snapshot
                                 } |> Some
                            | None -> None                            
                            |> Result.success
                    with | _ -> 
                        return! None |> Result.success
                }
            member __.DeleteSnapshots persistenceId criteria =
                operation {
                    let! eventStore = connect()
                    let! eventSlice = eventStore.ReadStreamEventsBackwardAsync(getMetadataStream persistenceId, -1L, readBatchSize, true, userCredentials = plugin.Credentials)

                    let! result =
                        eventSlice.Events
                        |> Seq.map deserializeSnapshotMetadata
                        |> Seq.filter (fun metadata -> metadata.SequenceNumber >= criteria.MinSequenceNr && metadata.SequenceNumber <= criteria.MaxSequenceNr && metadata.Timestamp >= criteria.MinTimestamp.GetValueOrDefault() && metadata.Timestamp = criteria.MaxTimeStamp)
                        |> Seq.map (fun metadata -> eventStore.DeleteStreamAsync(getStream metadata.PersistenceId metadata.SequenceNumber, ExpectedVersion.Any |> int64, userCredentials = plugin.Credentials))
                        |> Task.Parallel

                    return! Result.successWithEvents () [DeletedSuccessfully]
                }
            member __.DeleteAllSnapshots persistenceId sequenceNumber =
                operation {
                    let! eventStore = connect()
                    let! eventSlice = eventStore.ReadStreamEventsBackwardAsync(getMetadataStream persistenceId, -1L, readBatchSize, true, userCredentials = plugin.Credentials)

                    let! result = 
                        eventSlice.Events
                        |> Seq.map deserializeSnapshotMetadata
                        |> Seq.filter (fun snapshotMetadata -> snapshotMetadata.SequenceNumber <= sequenceNumber)
                        |> Seq.map (fun snapshotMetadata -> eventStore.DeleteStreamAsync(getStream snapshotMetadata.PersistenceId snapshotMetadata.SequenceNumber, ExpectedVersion.Any |> int64, userCredentials = plugin.Credentials))
                        |> Task.Parallel

                    return! Result.successWithEvents () [DeletedSuccessfully]
                }
        }

type EventStoreProvider() =
    interface IEventJournalProvider with
        member __.GetEventJournal config context = EventJournal.get config context

type EventStoreJournal (config: Config) =
    inherit StreamingEventJournal<EventStoreProvider>(config)
    static member Identifier = "akka.persistence.journal.event-store"

type EventStoreSnapshotStore (config: Config) =
    inherit StreamingSnapshotStore<EventStoreProvider>(config)
    static member Identifier = "akka.persistence.snapshot-store.event-store"