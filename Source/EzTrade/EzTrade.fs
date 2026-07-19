namespace EzTrade

open System
open System.Reflection
open System.Linq
open HarmonyLib
open RimWorld
open RimWorld.Planet
open UnityEngine
open Verse
open Verse.AI

module internal PawnExtensions =
    type Pawn with
        member internal pawn.TradePriceImprovement = pawn.GetStatValue(StatDefOf.TradePriceImprovement)

open PawnExtensions

module internal EzTradeOrders =
    let isAvailableTrader (pawn: Pawn) = not (isNull pawn.TraderKind) && pawn.CanTradeNow && not pawn.mindState.traderDismissed
    let canNegotiateWith (trader: Pawn) (pawn: Pawn) =
        pawn.CanTakeOrder
        && FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn).Accepted
        && not (isNull pawn.skills)
        && not (pawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
        && pawn.CanReach(LocalTargetInfo(trader), PathEndMode.OnCell, Danger.Deadly)
        && pawn.CanTradeWith(trader.Faction, trader.TraderKind).Accepted

    let traderAt clickPos =
        GenUI.ThingsUnderMouse(clickPos, 0.8f, TargetingParameters.ForPawns()).OfType<Pawn>().FirstOrDefault(isAvailableTrader) |> Option.ofObj

    let private bestNegotiatorFor (trader: Pawn) =
        match trader.Map with
        | null -> None
        | map ->
            map.mapPawns.AllPawnsSpawned
            |> Seq.filter (canNegotiateWith (trader))
            |> Seq.sortBy (fun pawn -> -pawn.TradePriceImprovement, pawn.thingIDNumber)
            |> Seq.tryHead

    let private startTrade (negotiator: Pawn) (trader: Pawn) =
        if not (isAvailableTrader trader) || not (canNegotiateWith trader negotiator) then
            let message = "EzTrade_TradeNoLongerAvailable".Translate(negotiator.LabelShort.Named("name")) |> string
            Messages.Message(message, LookTargets(trader), MessageTypeDefOf.RejectInput, false)
        else
            let job = JobMaker.MakeJob(JobDefOf.TradeWithPawn, LocalTargetInfo(trader))
            job.playerForced <- true
            negotiator.jobs.TryTakeOrderedJob(job, JobTag.Misc) |> ignore
            PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.InteractingWithTraders, KnowledgeAmount.Total)

    let makeOption (trader: Pawn) =
        match bestNegotiatorFor trader with
        | None -> FloatMenuOption("EzTrade_NoAvailableNegotiator".Translate() |> string, null)
        | Some negotiator ->
            let label =
                "EzTrade_TradeWith"
                    .Translate(negotiator.LabelShort.Named("name"), negotiator.TradePriceImprovement.ToStringPercent().Named("tradePriceImprovement"))
                |> string
            let option = FloatMenuOption(label, (fun () -> startTrade negotiator trader), MenuOptionPriority.InitiateSocial, null, trader)
            option.iconThing <- negotiator
            FloatMenuUtility.DecoratePrioritizedTask(option, negotiator, LocalTargetInfo(trader))

[<HarmonyPatch(typeof<Selector>, "HandleMapClicks")>]
module internal SelectorHandleMapClicksPatch =
    let Prefix () =
        let currentEvent = Event.current

        if currentEvent.``type`` <> EventType.MouseDown || currentEvent.button <> 1 || Find.Selector.SelectedPawns.Any(fun pawn -> pawn.CanTakeOrder) then
            true
        else
            let clickPos = UI.MouseMapPosition()

            match EzTradeOrders.traderAt clickPos with
            | None -> true
            | Some trader ->
                let menu = FloatMenu(ResizeArray [ EzTradeOrders.makeOption trader ])
                menu.givesColonistOrders <- true
                Find.WindowStack.Add(menu)
                currentEvent.Use()
                false

module internal PodTradeHelper =
    let getSettlement (action: TransportersArrivalAction_GiveGift) : Settlement = Traverse.Create(action).Field<Settlement>("settlement").Value

    let getItems (part: QuestPart_GiveToCaravan) : System.Collections.Generic.List<Thing> =
        Traverse.Create(part).Field<System.Collections.Generic.List<Thing>>("items").Value

    let getPawns (part: QuestPart_GiveToCaravan) : System.Collections.Generic.List<Pawn> =
        Traverse.Create(part).Field<System.Collections.Generic.List<Pawn>>("pawns").Value

    let countMatchingItems (transporters: seq<ActiveTransporterInfo>) (reqDef: ThingDef) =
        if isNull transporters then
            0
        else
            transporters
            |> Seq.filter (fun t -> not (isNull t.innerContainer))
            |> Seq.sumBy (fun t -> t.innerContainer |> Seq.filter (fun thing -> thing.def = reqDef) |> Seq.sumBy (fun thing -> thing.stackCount))

    let consumeRequiredItems (transporters: seq<ActiveTransporterInfo>) (reqDef: ThingDef) (reqCount: int) =
        let consumeFromContainer (container: ThingOwner) (amountNeeded: int) =
            if amountNeeded <= 0 then
                0
            else
                let things = container |> Seq.toList
                things
                |> List.fold
                    (fun remaining thing ->
                        if remaining <= 0 then
                            0
                        elif thing.def = reqDef then
                            let take = min thing.stackCount remaining
                            if take > 0 then
                                let taken = container.Take(thing, take)
                                if not (isNull taken) then
                                    taken.Destroy(DestroyMode.Vanish)
                            remaining - take
                        else
                            remaining)
                    amountNeeded

        if not (isNull transporters) then
            transporters
            |> Seq.filter (fun t -> not (isNull t.innerContainer))
            |> Seq.fold (fun remaining trans -> consumeFromContainer trans.innerContainer remaining) reqCount
            |> ignore

    let getQuestName (settlement: Settlement) =
        if isNull Find.QuestManager then
            "Trade Request"
        else
            Find.QuestManager.QuestsListForReading
            |> Seq.filter (fun q -> q.State = QuestState.Ongoing)
            |> Seq.tryPick (fun q ->
                q.PartsListForReading
                |> Seq.tryPick (fun part ->
                    match part with
                    | :? QuestPart_InitiateTradeRequest as initPart -> if initPart.settlement = settlement then Some q.name else None
                    | _ -> None))
            |> Option.defaultValue "Trade Request"

    let calculateLeftoverGoodwill (pods: seq<IThingHolder>) (settlement: Settlement) (reqDef: ThingDef) (reqCount: int) =
        if isNull pods || isNull settlement then
            0
        else
            let allMatchingThings =
                pods
                |> Seq.collect (fun pod ->
                    let container = pod.GetDirectlyHeldThings()
                    if isNull container then Seq.empty else container :> seq<Thing>)
                |> Seq.filter (fun t -> t.def = reqDef)
                |> Seq.toList

            let backups = allMatchingThings |> List.map (fun t -> t, t.stackCount)

            let rec reduce countRemaining (things: (Thing * int) list) =
                if countRemaining > 0 then
                    match things with
                    | [] -> ()
                    | (t, originalCount) :: tail ->
                        let consume = min originalCount countRemaining
                        t.stackCount <- originalCount - consume
                        reduce (countRemaining - consume) tail

            reduce reqCount backups
            let leftoverScore = FactionGiftUtility.GetGoodwillChange(pods, settlement)
            backups |> List.iter (fun (t, originalCount) -> t.stackCount <- originalCount)
            leftoverScore

    let getCompLaunchable (pods: seq<IThingHolder>) : CompLaunchable =
        if isNull pods || Seq.isEmpty pods then
            null
        else
            let firstPod = Seq.head pods
            if isNull firstPod then
                null
            else
                match firstPod with
                | :? CompTransporter as trans -> trans.Launchable
                | _ ->
                    match firstPod with
                    | :? Thing as t ->
                        let trans = t.TryGetComp<CompTransporter>()
                        if isNull trans then null else trans.Launchable
                    | _ ->
                        try
                            let launchableProp =
                                firstPod.GetType().GetProperty("Launchable", BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
                            if not (isNull launchableProp) then
                                launchableProp.GetValue(firstPod) :?> CompLaunchable
                            else
                                let transProp =
                                    firstPod.GetType().GetProperty("Transporter", BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
                                if not (isNull transProp) then
                                    let transObj = transProp.GetValue(firstPod) :?> CompTransporter
                                    if isNull transObj then null else transObj.Launchable
                                else
                                    let launchableField =
                                        firstPod.GetType().GetField("launchable", BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance)
                                    if not (isNull launchableField) then launchableField.GetValue(firstPod) :?> CompLaunchable else null
                        with _ ->
                            null

[<HarmonyPatch(typeof<TransportersArrivalAction_GiveGift>, "Arrived")>]
module TransportersArrivalAction_GiveGift_Patch =
    let Prefix (__instance: TransportersArrivalAction_GiveGift, transporters: System.Collections.Generic.List<ActiveTransporterInfo>, tile: PlanetTile) =
        try
            Log.Message("[EzTrade] TransportersArrivalAction_GiveGift.Arrived patch triggered")
            let tryFulfill (comp: TradeRequestComp) =
                let totalCount = PodTradeHelper.countMatchingItems transporters comp.requestThingDef
                Log.Message(sprintf "[EzTrade] Fulfill check - Required: %s x%d, In pods: %d" comp.requestThingDef.defName comp.requestCount totalCount)
                if totalCount >= comp.requestCount then
                    PodTradeHelper.consumeRequiredItems transporters comp.requestThingDef comp.requestCount

                    let parent = comp.parent
                    if not (isNull parent) then
                        let subjectArg = NamedArgument(parent, "SUBJECT")
                        QuestUtility.SendQuestTargetSignals(parent.questTags, "TradeRequestFulfilled", subjectArg)

                    comp.Disable()

                    let settlement = PodTradeHelper.getSettlement __instance
                    let msg = "PodTrade.Fulfilled".Translate(settlement.Label.Named("settlement")) |> string
                    Messages.Message(msg, MessageTypeDefOf.PositiveEvent, true)

            Option.ofObj (PodTradeHelper.getSettlement __instance)
            |> Option.bind (fun s -> Option.ofObj (s.GetComponent<TradeRequestComp>()))
            |> Option.filter (fun comp -> comp.ActiveRequest)
            |> Option.iter tryFulfill
        with ex ->
            Log.Error(sprintf "[EzTrade] Error in TransportersArrivalAction_GiveGift.Arrived Prefix patch: %O" ex)
        true

[<HarmonyPatch(typeof<TransportersArrivalAction_GiveGift>, "GetFloatMenuOptions")>]
module TransportersArrivalAction_GiveGift_GetFloatMenuOptions_Patch =
    let Prefix
        (
            __result: byref<System.Collections.Generic.IEnumerable<FloatMenuOption>>,
            __instance: TransportersArrivalAction_GiveGift,
            launchAction: Action<PlanetTile, TransportersArrivalAction>,
            pods: seq<IThingHolder>,
            settlement: Settlement
        ) =
        try
            Log.Message("[EzTrade] GetFloatMenuOptions Prefix patch triggered")
            if isNull settlement then
                __result <- Seq.empty
            else
                let report = TransportersArrivalAction_GiveGift.CanGiveGiftTo(pods, settlement)
                if not report.Accepted then
                    __result <- Seq.empty
                else
                    let tradeComp = settlement.GetComponent<TradeRequestComp>()
                    let optionList = System.Collections.Generic.List<FloatMenuOption>()

                    if not (isNull tradeComp) && tradeComp.ActiveRequest then
                        let countMatchingInPod (holder: IThingHolder) =
                            let container = holder.GetDirectlyHeldThings()
                            if isNull container then
                                0
                            else
                                container |> Seq.filter (fun thing -> thing.def = tradeComp.requestThingDef) |> Seq.sumBy (fun thing -> thing.stackCount)

                        let totalCount = if isNull pods then 0 else pods |> Seq.sumBy countMatchingInPod
                        Log.Message(sprintf "[EzTrade] Active request found. Matching count in pods: %d / %d" totalCount tradeComp.requestCount)

                        if totalCount >= tradeComp.requestCount then
                            let questName = PodTradeHelper.getQuestName settlement
                            let leftoverScore = PodTradeHelper.calculateLeftoverGoodwill pods settlement tradeComp.requestThingDef tradeComp.requestCount

                            let label =
                                if leftoverScore > 0 then
                                    "PodTrade.FulfillLeftover".Translate(questName.Named("questName"), leftoverScore.Named("score")) |> string
                                else
                                    "PodTrade.Fulfill".Translate(questName.Named("questName")) |> string

                            let action =
                                Action(fun () ->
                                    let title = "PodTrade.ConfirmTitle".Translate() |> string
                                    let text =
                                        if leftoverScore > 0 then
                                            "PodTrade.ConfirmDescLeftover"
                                                .Translate(
                                                    settlement.Label.Named("settlement"),
                                                    tradeComp.requestCount.Named("requestThingCount"),
                                                    tradeComp.requestThingDef.LabelCap.Resolve().Named("requestedThingLabel"),
                                                    leftoverScore.Named("score")
                                                )
                                            |> string
                                        else
                                            "PodTrade.ConfirmDesc"
                                                .Translate(
                                                    settlement.Label.Named("settlement"),
                                                    tradeComp.requestCount.Named("requestThingCount"),
                                                    tradeComp.requestThingDef.LabelCap.Resolve().Named("requestedThingLabel")
                                                )
                                            |> string
                                    let dialog =
                                        Dialog_MessageBox.CreateConfirmation(
                                            text,
                                            (fun () ->
                                                let compLaunchable = PodTradeHelper.getCompLaunchable pods
                                                let customArrivalAction = TransportersArrivalAction_GiveGift(settlement)
                                                if not (isNull compLaunchable) then
                                                    compLaunchable.TryLaunch(PlanetTile(settlement.Tile), customArrivalAction)
                                                else
                                                    launchAction.Invoke(PlanetTile(settlement.Tile), customArrivalAction)),
                                            true,
                                            title
                                        )
                                    Find.WindowStack.Add(dialog))

                            let opt = FloatMenuOption(label, action)
                            Traverse.Create(opt).Field("labelInt").SetValue(label) |> ignore
                            optionList.Add(opt)
                        else
                            let goodwillChange = FactionGiftUtility.GetGoodwillChange(pods, settlement)
                            let label =
                                "GiveGiftViaTransportPods".Translate(settlement.Faction.Name.Named("factionName"), goodwillChange.Named("goodwillChange"))
                                |> string
                            let opt = FloatMenuOption(label, Action(fun () -> launchAction.Invoke(PlanetTile(settlement.Tile), __instance)))
                            optionList.Add(opt)
                    else
                        let goodwillChange = FactionGiftUtility.GetGoodwillChange(pods, settlement)
                        let label =
                            "GiveGiftViaTransportPods".Translate(settlement.Faction.Name.Named("factionName"), goodwillChange.Named("goodwillChange")) |> string
                        let opt = FloatMenuOption(label, Action(fun () -> launchAction.Invoke(PlanetTile(settlement.Tile), __instance)))
                        optionList.Add(opt)

                    __result <- (optionList :> System.Collections.Generic.IEnumerable<FloatMenuOption>)
        with ex ->
            Log.Error(sprintf "[EzTrade] Error in GetFloatMenuOptions Prefix: %O" ex)
            __result <- Seq.empty
        false

[<HarmonyPatch(typeof<QuestPart_GiveToCaravan>, "Notify_QuestSignalReceived")>]
module QuestPart_GiveToCaravan_Patch =
    let Prefix (__instance: QuestPart_GiveToCaravan, signal: RimWorld.Signal) =
        if signal.tag = __instance.inSignal && (isNull __instance.caravan || not __instance.caravan.Spawned) then
            Option.ofObj Find.AnyPlayerHomeMap
            |> Option.iter (fun map ->
                let items = PodTradeHelper.getItems __instance
                let pawns = PodTradeHelper.getPawns __instance
                let dropThings = System.Collections.Generic.List<Thing>()

                if not (isNull items) then
                    items |> Seq.filter (fun t -> not t.Spawned) |> Seq.iter dropThings.Add
                if not (isNull pawns) then
                    pawns |> Seq.filter (fun p -> not p.Spawned) |> Seq.iter dropThings.Add

                if dropThings.Count > 0 then
                    DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, dropThings, 0, false, false, true, false, true, null)
                    Messages.Message("PodTrade.RewardsDropped".Translate() |> string, MessageTypeDefOf.PositiveEvent, true)
                    if not (isNull items) then
                        items.Clear()
                    if not (isNull pawns) then
                        pawns.Clear())
        true

[<StaticConstructorOnStartup>]
type EzTradeBootstrap() =
    static do Harmony("scarf.eztrade").PatchAll()
