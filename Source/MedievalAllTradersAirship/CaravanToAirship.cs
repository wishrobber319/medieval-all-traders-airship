using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace MedievalAllTradersAirship
{
    // Convert faction trade caravans into landing airships. Medieval Trader Airships only redirects
    // orbital traders; faction caravans still walk in on foot. This redirects the vanilla
    // TraderCaravanArrival incident to spawn one of Medieval Trader Airships' own landing trade ships,
    // stocked from the faction's caravan trader kind, and skips the walking pack-animal group.
    //
    // It binds to Medieval Trader Airships' internals by name (no compile-time reference), so if that
    // mod is absent or changed we cleanly fall back to the normal walking caravan.
    [StaticConstructorOnStartup]
    public static class CaravanToAirship
    {
        private const string Tag = "[All Traders Airship]";

        private static ThingDef shipDef;
        private static Type compShipType;
        private static MethodInfo generateInternalTradeShip; // CompShip.GenerateInternalTradeShip(Map, TraderKindDef)
        private static MethodInfo landShip;                  // IncidentWorkerTraderShip.LandShip(Map, Thing)
        private static MethodInfo tryResolveParms;           // IncidentWorker_TraderCaravanArrival.TryResolveParms(IncidentParms)
        private static bool ready;

        static CaravanToAirship()
        {
            var harmony = new Harmony("wishRobber.alltradersairship");

            Type globals = AccessTools.TypeByName("Joe_Airships.Globals");
            compShipType = AccessTools.TypeByName("Joe_Airships.CompShip");
            Type incidentWorker = AccessTools.TypeByName("Joe_Airships.IncidentWorkerTraderShip");
            if (globals == null || compShipType == null || incidentWorker == null)
            {
                Log.Warning($"{Tag} Medieval Trader Airships not found - faction caravans left as normal walking caravans.");
                return;
            }

            shipDef = AccessTools.Field(globals, "TraderShipsShip")?.GetValue(null) as ThingDef;
            generateInternalTradeShip = AccessTools.Method(compShipType, "GenerateInternalTradeShip",
                new[] { typeof(Map), typeof(TraderKindDef) });
            landShip = AccessTools.Method(incidentWorker, "LandShip", new[] { typeof(Map), typeof(Thing) });

            ready = shipDef != null && generateInternalTradeShip != null && landShip != null;
            if (!ready)
            {
                Log.Warning($"{Tag} could not bind Medieval Trader Airships internals - faction caravans left as normal walking caravans.");
                return;
            }

            // Vanilla resolves the faction + trader kind at the START of TryExecuteWorker (which we skip),
            // so we need to run that same resolution ourselves before reading parms.traderKind.
            tryResolveParms = AccessTools.Method(typeof(IncidentWorker_TraderCaravanArrival), "TryResolveParms",
                new[] { typeof(IncidentParms) });

            // TryExecuteWorker is overridden on IncidentWorker_TraderCaravanArrival, so this only affects
            // trade caravans (not visitor/traveler groups, which use different worker classes).
            harmony.Patch(
                AccessTools.Method(typeof(IncidentWorker_TraderCaravanArrival), "TryExecuteWorker"),
                prefix: new HarmonyMethod(typeof(CaravanToAirship), nameof(TryExecutePrefix)));

            Log.Message($"{Tag} faction trade caravans will arrive by airship.");
        }

        public static bool TryExecutePrefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (!ready)
            {
                return true; // fall back to the vanilla walking caravan
            }

            Map map = parms.target as Map;
            if (map == null)
            {
                return true;
            }

            // Resolve the faction + caravan trader kind exactly as the vanilla worker does at the top of
            // TryExecuteWorker (which we're skipping). Without this, parms.traderKind is still null here.
            if (parms.traderKind == null)
            {
                if (tryResolveParms == null || !(tryResolveParms.Invoke(__instance, new object[] { parms }) is bool ok) || !ok)
                {
                    return true; // couldn't resolve a valid caravan; let the vanilla incident handle it
                }
            }

            TraderKindDef caravanKind = parms.traderKind;
            if (caravanKind == null)
            {
                return true; // nothing resolved; let the vanilla caravan happen
            }

            try
            {
                Thing ship = ThingMaker.MakeThing(shipDef, null);
                ThingComp comp = (ship as ThingWithComps)?.AllComps
                    .FirstOrDefault(c => compShipType.IsInstanceOfType(c));
                if (comp == null)
                {
                    return true;
                }

                // Stock the airship from the faction's caravan trader kind, then land it. The ship
                // sends its own arrival letter on spawn (CompShip.PostSpawnSetup).
                generateInternalTradeShip.Invoke(comp, new object[] { map, caravanKind });
                landShip.Invoke(null, new object[] { map, ship });

                __result = true;
                return false; // skip the walking pack-animal caravan
            }
            catch (Exception e)
            {
                Log.Warning($"{Tag} airship redirect failed, falling back to a walking caravan: {e.InnerException?.Message ?? e.Message}");
                return true;
            }
        }
    }
}
