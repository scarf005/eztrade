namespace EzTrade

open System
open System.Collections.Generic
open System.Reflection
open HarmonyLib
open RimWorld
open RimWorld.Planet
open UnityEngine
open Verse
open Verse.AI
open Verse.AI.Group

[<AutoOpen>]
module internal Extensions =
    type Pawn with
        member internal pawn.TradePriceImprovement = pawn.GetStatValue(StatDefOf.TradePriceImprovement)

module internal Fields =
    let giftSettlement = AccessTools.FieldRefAccess<TransportersArrivalAction_GiveGift, Settlement>("settlement")
    let caravanItems = AccessTools.FieldRefAccess<QuestPart_GiveToCaravan, List<Thing> | null>("items")
    let caravanPawns = AccessTools.FieldRefAccess<QuestPart_GiveToCaravan, List<Pawn> | null>("pawns")

module internal EzTradeOrders =
    type private Order =
        { IsTarget: Pawn -> bool
          CanOrder: Pawn -> Pawn -> bool
          Job: JobDef
          LabelKey: string
          NoPawnKey: string
          UnavailableKey: string
          OnStarted: unit -> unit }

    let isAvailableTrader (pawn: Pawn) = pawn.TraderKind <> null && pawn.CanTradeNow && not pawn.mindState.traderDismissed

    let private isRatkinWanderingCaravanLeader (pawn: Pawn) =
        match pawn.GetLord() with
        | null -> false
        | lord ->
            match lord.LordJob with
            | NonNull job when job.GetType().FullName = "NewRatkin.LordJob_WanderingCaravan" -> Traverse.Create(job).Field<Pawn>("leader").Value = pawn
            | _ -> false

    let canNegotiateWith (trader: Pawn) (pawn: Pawn) =
        pawn.CanTakeOrder
        && FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn).Accepted
        && pawn.skills <> null
        && not (pawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
        && pawn.CanReach(LocalTargetInfo(trader), PathEndMode.OnCell, Danger.Deadly)
        && pawn.CanTradeWith(trader.Faction, trader.TraderKind).Accepted

    let private canTalkToRatkinLeader (trader: Pawn) (pawn: Pawn) =
        pawn.IsColonist
        && pawn.CanTakeOrder
        && FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn).Accepted
        && not (StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(pawn))
        && pawn.CanReach(LocalTargetInfo(trader), PathEndMode.Touch, Danger.Deadly)

    let traderAt clickPos =
        GenUI.ThingsUnderMouse(clickPos, 0.8f, TargetingParameters.ForPawns())
        |> Seq.tryPick (function
            | :? Pawn as pawn when isAvailableTrader pawn || isRatkinWanderingCaravanLeader pawn -> Some pawn
            | _ -> None)

    let private bestNegotiatorFor (trader: Pawn) canOrder =
        match trader.Map with
        | null -> None
        | map ->
            map.mapPawns.AllPawnsSpawned
            |> Seq.filter (canOrder trader)
            |> Seq.sortBy (fun pawn -> -pawn.TradePriceImprovement, pawn.thingIDNumber)
            |> Seq.tryHead

    let private start (order: Order) (negotiator: Pawn) (trader: Pawn) =
        if not (order.IsTarget trader) || not (order.CanOrder trader negotiator) then
            Messages.Message(order.UnavailableKey.Translate(negotiator.LabelShort.Named("name")), LookTargets(trader), MessageTypeDefOf.RejectInput, false)
        else
            let job = JobMaker.MakeJob(order.Job, LocalTargetInfo(trader))
            job.playerForced <- true
            negotiator.jobs.TryTakeOrderedJob(job, JobTag.Misc) |> ignore
            order.OnStarted()

    let private orderOption (order: Order) (trader: Pawn) =
        match bestNegotiatorFor trader order.CanOrder with
        | None -> FloatMenuOption(string (order.NoPawnKey.Translate()), null)
        | Some negotiator ->
            let label =
                order.LabelKey.Translate(negotiator.LabelShort.Named("name"), negotiator.TradePriceImprovement.ToStringPercent().Named("tradePriceImprovement"))

            let option = FloatMenuOption(string label, (fun () -> start order negotiator trader), MenuOptionPriority.InitiateSocial, null, trader)
            option.iconThing <- negotiator
            FloatMenuUtility.DecoratePrioritizedTask(option, negotiator, LocalTargetInfo(trader))

    let private ratkinTalkJob (trader: Pawn) =
        if isRatkinWanderingCaravanLeader trader then DefDatabase<JobDef>.GetNamedSilentFail("RK_Job_TalkToCaravanLeader") |> Option.ofObj else None

    let makeOption (trader: Pawn) =
        let order =
            match ratkinTalkJob trader with
            | Some talkJob ->
                { IsTarget = isRatkinWanderingCaravanLeader
                  CanOrder = canTalkToRatkinLeader
                  Job = talkJob
                  LabelKey = "EzTrade_TalkWithRatkinCaravanLeader"
                  NoPawnKey = "EzTrade_NoAvailableSpeaker"
                  UnavailableKey = "EzTrade_TalkNoLongerAvailable"
                  OnStarted = ignore }
            | None ->
                { IsTarget = isAvailableTrader
                  CanOrder = canNegotiateWith
                  Job = JobDefOf.TradeWithPawn
                  LabelKey = "EzTrade_TradeWith"
                  NoPawnKey = "EzTrade_NoAvailableNegotiator"
                  UnavailableKey = "EzTrade_TradeNoLongerAvailable"
                  OnStarted = fun () -> PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.InteractingWithTraders, KnowledgeAmount.Total) }

        orderOption order trader

[<HarmonyPatch(typeof<Selector>, "HandleMapClicks")>]
module internal SelectorHandleMapClicksPatch =
    let Prefix () =
        let currentEvent = Event.current

        if currentEvent.``type`` <> EventType.MouseDown || currentEvent.button <> 1 || Find.Selector.SelectedPawns |> Seq.exists _.CanTakeOrder then
            true
        else
            match EzTradeOrders.traderAt (UI.MouseMapPosition()) with
            | None -> true
            | Some trader ->
                let menu = FloatMenu(ResizeArray [ EzTradeOrders.makeOption trader ])
                menu.givesColonistOrders <- true
                Find.WindowStack.Add(menu)
                currentEvent.Use()
                false

module internal PodTradeHelper =
    let private present (xs: seq<'T | null> | null) : seq<'T> =
        match xs with
        | null -> Seq.empty
        | xs -> Seq.choose Option.ofObj xs

    let private containers (transporters: seq<ActiveTransporterInfo | null> | null) = present transporters |> Seq.choose (_.innerContainer >> Option.ofObj)

    let private heldThings (pods: seq<IThingHolder | null> | null) : seq<Thing> =
        present pods
        |> Seq.collect (fun pod ->
            match pod.GetDirectlyHeldThings() with
            | null -> Seq.empty
            | things -> Seq.cast things)

    let private countOf (reqDef: ThingDef) (things: seq<Thing>) = things |> Seq.filter (fun thing -> thing.def = reqDef) |> Seq.sumBy _.stackCount

    let countMatchingItems transporters reqDef = containers transporters |> Seq.collect id |> countOf reqDef

    let countMatchingInPods pods reqDef = heldThings pods |> countOf reqDef

    let consumeRequiredItems transporters (reqDef: ThingDef) (reqCount: int) =
        let consumeFromContainer (remaining: int) (container: ThingOwner) =
            container
            |> Seq.toList
            |> List.fold
                (fun remaining thing ->
                    if remaining <= 0 || thing.def <> reqDef then
                        remaining
                    else
                        let take = min thing.stackCount remaining

                        if take > 0 then
                            match container.Take(thing, take) with
                            | null -> ()
                            | taken -> taken.Destroy(DestroyMode.Vanish)

                        remaining - take)
                remaining

        containers transporters |> Seq.fold consumeFromContainer reqCount |> ignore

    let getQuestName (settlement: Settlement) =
        let fallback = "Trade Request"

        match Find.QuestManager with
        | null -> fallback
        | quests ->
            quests.QuestsListForReading
            |> Seq.filter (fun quest -> quest.State = QuestState.Ongoing)
            |> Seq.tryPick (fun quest ->
                quest.PartsListForReading
                |> Seq.tryPick (function
                    | :? QuestPart_InitiateTradeRequest as part when part.settlement = settlement -> Some quest.name
                    | _ -> None))
            |> Option.defaultValue fallback

    /// Goodwill from the gift that remains after the requested items are removed.
    let calculateLeftoverGoodwill (pods: seq<IThingHolder> | null) (settlement: Settlement) (reqDef: ThingDef) (reqCount: int) =
        match pods with
        | null -> 0
        | pods ->
            let backups = heldThings pods |> Seq.filter (fun thing -> thing.def = reqDef) |> Seq.map (fun thing -> thing, thing.stackCount) |> Seq.toList

            backups
            |> List.fold
                (fun remaining (thing, count) ->
                    let consume = min count remaining
                    thing.stackCount <- count - consume
                    remaining - consume)
                reqCount
            |> ignore

            try
                FactionGiftUtility.GetGoodwillChange(pods, settlement)
            finally
                for thing, count in backups do
                    thing.stackCount <- count

    let private launchableOf (transporter: CompTransporter | null) =
        match transporter with
        | null -> null
        | transporter -> transporter.Launchable

    let private reflectLaunchable (pod: IThingHolder) : CompLaunchable | null =
        let instanceMembers = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance
        let podType = pod.GetType()

        try
            match podType.GetProperty("Launchable", instanceMembers), podType.GetProperty("Transporter", instanceMembers) with
            | NonNull launchable, _ -> launchable.GetValue(pod) :?> (CompLaunchable | null)
            | Null, NonNull transporter -> transporter.GetValue(pod) :?> (CompTransporter | null) |> launchableOf
            | Null, Null ->
                match podType.GetField("launchable", instanceMembers) with
                | null -> null
                | field -> field.GetValue(pod) :?> (CompLaunchable | null)
        with _ ->
            null

    let getCompLaunchable (pods: seq<IThingHolder | null> | null) : CompLaunchable | null =
        match pods |> Option.ofObj |> Option.bind Seq.tryHead with
        | None
        | Some Null -> null
        | Some(NonNull(:? CompTransporter as transporter)) -> transporter.Launchable
        | Some(NonNull(:? Thing as thing)) -> thing.TryGetComp<CompTransporter>() |> launchableOf
        | Some(NonNull pod) -> reflectLaunchable pod

[<HarmonyPatch(typeof<TransportersArrivalAction_GiveGift>, "Arrived")>]
module TransportersArrivalAction_GiveGift_Patch =
    let Prefix (__instance: TransportersArrivalAction_GiveGift, transporters: List<ActiveTransporterInfo>, tile: PlanetTile) =
        try
            let settlement = Fields.giftSettlement.Invoke(__instance)

            match settlement |> Option.ofObj |> Option.bind (_.GetComponent<TradeRequestComp>() >> Option.ofObj) with
            | Some comp when comp.ActiveRequest && PodTradeHelper.countMatchingItems transporters comp.requestThingDef >= comp.requestCount ->
                PodTradeHelper.consumeRequiredItems transporters comp.requestThingDef comp.requestCount

                match comp.parent with
                | null -> ()
                | parent -> QuestUtility.SendQuestTargetSignals(parent.questTags, "TradeRequestFulfilled", NamedArgument(parent, "SUBJECT"))

                comp.Disable()
                Messages.Message("PodTrade.Fulfilled".Translate(settlement.Label.Named("settlement")), MessageTypeDefOf.PositiveEvent, true)
            | _ -> ()
        with ex ->
            Log.Error $"[EzTrade] Error in TransportersArrivalAction_GiveGift.Arrived Prefix patch: {ex}"

        true

[<HarmonyPatch(typeof<TransportersArrivalAction_GiveGift>, nameof TransportersArrivalAction_GiveGift.GetFloatMenuOptions)>]
module TransportersArrivalAction_GiveGift_GetFloatMenuOptions_Patch =
    let private giftOption (pods: seq<IThingHolder>) (settlement: Settlement) (launchAction: Action<PlanetTile, TransportersArrivalAction>) =
        let goodwillChange = FactionGiftUtility.GetGoodwillChange(pods, settlement)
        let label = "GiveGiftViaTransportPods".Translate(settlement.Faction.Name.Named("factionName"), goodwillChange.Named("goodwillChange"))
        FloatMenuOption(label, (fun () -> launchAction.Invoke(PlanetTile(settlement.Tile), TransportersArrivalAction_GiveGift(settlement))))

    let private fulfillOption
        (pods: seq<IThingHolder>)
        (settlement: Settlement)
        (launchAction: Action<PlanetTile, TransportersArrivalAction>)
        (comp: TradeRequestComp)
        =
        let questName = PodTradeHelper.getQuestName settlement
        let leftoverScore = PodTradeHelper.calculateLeftoverGoodwill pods settlement comp.requestThingDef comp.requestCount
        let settlementArg = settlement.Label.Named("settlement")
        let countArg = comp.requestCount.Named("requestThingCount")
        let thingArg = comp.requestThingDef.LabelCap.Resolve().Named("requestedThingLabel")

        let label, text =
            if leftoverScore > 0 then
                "PodTrade.FulfillLeftover".Translate(questName.Named("questName"), leftoverScore.Named("score")),
                "PodTrade.ConfirmDescLeftover".Translate(settlementArg, countArg, thingArg, leftoverScore.Named("score"))
            else
                "PodTrade.Fulfill".Translate(questName.Named("questName")), "PodTrade.ConfirmDesc".Translate(settlementArg, countArg, thingArg)

        let launch () =
            let arrivalAction = TransportersArrivalAction_GiveGift(settlement)

            match PodTradeHelper.getCompLaunchable pods with
            | null -> launchAction.Invoke(PlanetTile(settlement.Tile), arrivalAction)
            | launchable -> launchable.TryLaunch(PlanetTile(settlement.Tile), arrivalAction)

        let confirm () = Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, launch, true, "PodTrade.ConfirmTitle".Translate()))

        FloatMenuOption(string label, confirm)

    let Prefix
        (
            __result: byref<IEnumerable<FloatMenuOption>>,
            launchAction: Action<PlanetTile, TransportersArrivalAction>,
            pods: seq<IThingHolder>,
            settlement: Settlement | null
        ) =
        try
            __result <-
                match settlement with
                | null -> Seq.empty
                | settlement when not (TransportersArrivalAction_GiveGift.CanGiveGiftTo(pods, settlement).Accepted) -> Seq.empty
                | settlement ->
                    match settlement.GetComponent<TradeRequestComp>() with
                    | NonNull comp when comp.ActiveRequest && PodTradeHelper.countMatchingInPods pods comp.requestThingDef >= comp.requestCount ->
                        [ fulfillOption pods settlement launchAction comp ]
                    | _ -> [ giftOption pods settlement launchAction ]
        with ex ->
            Log.Error $"[EzTrade] Error in GetFloatMenuOptions Prefix: {ex}"
            __result <- Seq.empty

        false

module internal ImpassableLaunch =
    let addOceanOption (options: seq<FloatMenuOption>) (tile: PlanetTile) (launchAction: Action<PlanetTile, TransportersArrivalAction | null>) =
        if tile.Valid && Find.World.Impassable(tile) then
            let oceanOption =
                FloatMenuOption(string ("EzTrade_DumpInOcean".Translate()), (fun () -> launchAction.Invoke(tile, null)), MenuOptionPriority.Default, null, null)
            Seq.append options [ oceanOption ]
        else
            options

[<HarmonyPatch(typeof<CompLaunchable>, "GetTransportersFloatMenuOptionsAt")>]
module GetTransportersFloatMenuOptionsAt_Patch =
    let Postfix (__result: seq<FloatMenuOption>, tile: PlanetTile, launchAction: Action<PlanetTile, TransportersArrivalAction | null>) =
        ImpassableLaunch.addOceanOption __result tile launchAction

[<HarmonyPatch(typeof<CompLaunchable>, nameof CompLaunchable.GetOptionsForTile)>]
module GetOptionsForTile_Patch =
    let Postfix (__result: seq<FloatMenuOption>, tile: PlanetTile, launchAction: Action<PlanetTile, TransportersArrivalAction | null>) =
        ImpassableLaunch.addOceanOption __result tile launchAction

[<HarmonyPatch(typeof<QuestPart_GiveToCaravan>, "Notify_QuestSignalReceived")>]
module QuestPart_GiveToCaravan_Patch =
    let Prefix (__instance: QuestPart_GiveToCaravan, signal: RimWorld.Signal) =
        let caravanGone =
            match __instance.caravan with
            | null -> true
            | caravan -> not caravan.Spawned

        if signal.tag = __instance.inSignal && caravanGone then
            match Find.AnyPlayerHomeMap with
            | null -> ()
            | map ->
                let items = Fields.caravanItems.Invoke(__instance)
                let pawns = Fields.caravanPawns.Invoke(__instance)
                let unspawned (things: List<'T> | null) : seq<Thing> =
                    match things with
                    | null -> Seq.empty
                    | things -> things |> Seq.cast<Thing> |> Seq.filter (fun thing -> not thing.Spawned)

                let dropThings = List<Thing>(Seq.append (unspawned items) (unspawned pawns))

                if dropThings.Count > 0 then
                    DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, dropThings, 0, false, false, true, false, true, null)
                    Messages.Message("PodTrade.RewardsDropped".Translate(), MessageTypeDefOf.PositiveEvent, true)

                    for list in [ items :> Collections.IList | null; pawns ] do
                        match list with
                        | null -> ()
                        | list -> list.Clear()

        true

[<StaticConstructorOnStartup>]
type EzTradeBootstrap() =
    static do Harmony("scarf.eztrade").PatchAll()
