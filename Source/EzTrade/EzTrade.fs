namespace EzTrade

open System.Linq
open HarmonyLib
open RimWorld
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

[<StaticConstructorOnStartup>]
type EzTradeBootstrap() =
    static do Harmony("scarf.eztrade").PatchAll()
