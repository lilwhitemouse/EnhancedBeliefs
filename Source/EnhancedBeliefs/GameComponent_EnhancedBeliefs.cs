﻿using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Noise;

namespace EnhancedBeliefs
{
    public class GameComponent_EnhancedBeliefs : GameComponent
    {
        // Days to percentage
        public static readonly SimpleCurve CertaintyLossFromInactivity = new SimpleCurve
        {
            new CurvePoint(3f,  0.01f),
            new CurvePoint(5f,  0.02f),
            new CurvePoint(10f, 0.03f),
            new CurvePoint(30f, 0.05f),
        };

        // Sum mood offset to percentage
        public static readonly SimpleCurve CertaintyOffsetFromThoughts = new SimpleCurve
        {
            new CurvePoint(-50f, -0.15f),
            new CurvePoint(-30f, -0.07f),
            new CurvePoint(-10f, -0.03f),
            new CurvePoint(-5f,  -0.01f),
            new CurvePoint(-3f,  -0.005f),
            new CurvePoint(-0,    0f),
            new CurvePoint(3f,    0.001f),
            new CurvePoint(5f,    0.003f),
            new CurvePoint(10f,   0.01f),
            new CurvePoint(30f,   0.07f),
            new CurvePoint(50f,   0.15f),
        };

        // Sum relationship value to multiplier - 1. Values are flipped if summary mood offset is negative
        // I know that this doesn't actually result in symmetric multipliers, but else we'll get x10 certainty loss if you hate everyone and everything in your ideology
        public static readonly SimpleCurve CertaintyMultiplierFromRelationships = new SimpleCurve
        {
            new CurvePoint(-1000f, -0.9f),
            new CurvePoint(-500f,  -0.5f),
            new CurvePoint(-200f,  -0.3f),
            new CurvePoint(-100f,  -0.1f),
            new CurvePoint(-50f,   -0.05f),
            new CurvePoint(-10f,   -0.02f),
            new CurvePoint(0f,     -0f),
            new CurvePoint(10f,     0.01f),
            new CurvePoint(50f,     0.03f),
            new CurvePoint(100f,    0.07f),
            new CurvePoint(200f,    0.2f),
            new CurvePoint(500f,    0.4f),
            new CurvePoint(1000f,   0.6f),
        };

        public Dictionary<Pawn, IdeoTrackerData> pawnTrackerData = new Dictionary<Pawn, IdeoTrackerData> ();
        public Dictionary<Ideo, List<Pawn>> ideoPawnsList = new Dictionary<Ideo, List<Pawn>>();

        public GameComponent_EnhancedBeliefs(Game game) { }

        public void AddTracker(Pawn pawn)
        {
            IdeoTrackerData data = new IdeoTrackerData(pawn);
            pawnTrackerData[pawn] = data;
        }

        public void AddIdeoTracker(Ideo ideo)
        {
            ideoPawnsList[ideo] = new List<Pawn>();
        }

        public void SetIdeo(Pawn pawn, Ideo ideo)
        {
            List<Ideo> ideoList = ideoPawnsList.Keys.ToList();

            for (int i = 0; i < ideoList.Count; i++)
            {
                Ideo ideo2 = ideoList[i];

                if (ideoPawnsList[ideo2].Contains(pawn))
                {
                    ideoPawnsList[ideo2].Remove(pawn);
                }
            }

            if (!ideoPawnsList.ContainsKey(ideo))
            {
                AddIdeoTracker(ideo);
            }

            if (!ideoPawnsList[ideo].Contains(pawn))
            {
                ideoPawnsList[ideo].Add(pawn);
            }
        }

        public static int BeliefDifferences(Ideo ideo1, Ideo ideo2)
        {
            int value = 0;

            for (int i = 0; i < ideo1.memes.Count; i++)
            {
                MemeDef meme1 = ideo1.memes[i];

                for (int j = 0; j < ideo2.memes.Count; j++)
                {
                    MemeDef meme2 = ideo2.memes[j];

                    if (meme1 == meme2)
                    {
                        value -= 1;
                    }
                    else if (meme1.exclusionTags.Intersect(meme2.exclusionTags).Count() > 0)
                    {
                        value += 1;
                    }
                }
            }

            return value;
        }

        public void FluidIdeoRecache(Ideo ideo)
        {
            foreach (KeyValuePair<Pawn, IdeoTrackerData> pair in pawnTrackerData)
            {
                pair.Value.baseIdeoOpinions[ideo] = pair.Value.DefaultIdeoOpinion(ideo);
            }
        }

        public List<Pawn> GetIdeoPawns(Ideo ideo)
        {
            if (ideoPawnsList.ContainsKey(ideo))
            {
                return ideoPawnsList[ideo];
            }

            ideoPawnsList[ideo] = new List<Pawn>();
            List<Pawn> pawns = PawnsFinder.All_AliveOrDead;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];

                if (pawn.ideo != null && pawn.Ideo == ideo)
                {
                    ideoPawnsList[ideo].Add(pawn);
                }
            }
            return ideoPawnsList[ideo];
        }
    }

    public class IdeoTrackerData : IExposable
    {
        public Pawn pawn;
        public int lastPositiveThoughtTick = -1;
        public float cachedCertaintyChange = -9999f;

        // Separate because recalculating base from memes in case player's ideo is fluid cuts down on overall performance cost
        // Breaks if you multiply opinion but you really shouldn't do that
        public Dictionary<Ideo, float> baseIdeoOpinions = new Dictionary<Ideo, float>();
        public Dictionary<Ideo, float> personalIdeoOpinions = new Dictionary<Ideo, float>();
        public Dictionary<Ideo, float> cachedRelationshipIdeoOpinions = new Dictionary<Ideo, float>();

        public Dictionary<MemeDef, float> memeOpinions = new Dictionary<MemeDef, float>();
        public Dictionary<PreceptDef, float> preceptOpinions = new Dictionary<PreceptDef, float>();

        private List<Ideo> cache1;
        private List<Ideo> cache2;
        private List<MemeDef> cache3;
        private List<PreceptDef> cache4;
        private List<float> cache5;
        private List<float> cache6;
        private List<float> cache7;
        private List<float> cache8;

        public IdeoTrackerData()
        {

        }

        public IdeoTrackerData(Pawn pawn)
        {
            lastPositiveThoughtTick = Find.TickManager.TicksGame;
            this.pawn = pawn;
        }

        public void CertaintyChangeRecache(GameComponent_EnhancedBeliefs worldComp)
        {
            cachedCertaintyChange = 0;
            List<Thought> thoughts = new List<Thought>();
            pawn.needs.mood.thoughts.GetAllMoodThoughts(thoughts);
            float moodSum = 0;

            for (int i = 0; i < thoughts.Count; i++)
            {
                Thought thought = thoughts[i];

                if (thought.sourcePrecept != null || thought.def.Worker is ThoughtWorker_Precept)
                {
                    moodSum += thought.MoodOffset();
                }
            }

            float moodCertaintyOffset = GameComponent_EnhancedBeliefs.CertaintyOffsetFromThoughts.Evaluate(moodSum);
            float relationshipMultiplier = 1 + GameComponent_EnhancedBeliefs.CertaintyMultiplierFromRelationships.Evaluate(IdeoOpinionFromRelationships(pawn.Ideo) / 0.02f) * Math.Sign(moodCertaintyOffset);

            cachedCertaintyChange += moodCertaintyOffset * relationshipMultiplier;
        }

        // Form opinion based on memes, personal thoughts and experience with other pawns from that ideo
        public float IdeoOpinion(Ideo ideo)
        {
            if (!baseIdeoOpinions.ContainsKey(ideo))
            {
                baseIdeoOpinions[ideo] = DefaultIdeoOpinion(ideo);
                personalIdeoOpinions[ideo] = 0;
            }

            if (ideo == pawn.Ideo)
            {
                baseIdeoOpinions[ideo] = pawn.ideo.Certainty * 100f;
            }

            return Mathf.Clamp(baseIdeoOpinions[ideo] + PersonalIdeoOpinion(ideo) + IdeoOpinionFromRelationships(ideo), 0, 100) / 100f;
        }

        // Rundown on the function above, for UI reasons
        public float[] DetailedIdeoOpinion(Ideo ideo)
        {
            if (!baseIdeoOpinions.ContainsKey(ideo))
            {
                IdeoOpinion(ideo);
            }

            if (ideo == pawn.Ideo)
            {
                baseIdeoOpinions[ideo] = pawn.ideo.Certainty * 100f;
            }

            return new float[3] { baseIdeoOpinions[ideo] / 100f, PersonalIdeoOpinion(ideo) / 100f, IdeoOpinionFromRelationships(ideo) / 100f };
        }

        // Get pawn's basic opinion from hearing about ideos beliefs, based on their traits, relationships and current ideo
        public float DefaultIdeoOpinion(Ideo ideo)
        {
            Ideo pawnIdeo = pawn.Ideo;

            if (ideo == pawnIdeo)
            {
                return pawn.ideo.Certainty * 100f;
            }

            float opinion = 30;

            if (ideo.HasMeme(EnhancedBeliefsDefOf.Supremacist))
            {
                opinion -= 20;
            }
            else if (ideo.HasMeme(EnhancedBeliefsDefOf.Loyalist))
            {
                opinion -= 10;
            }
            else if (ideo.HasMeme(EnhancedBeliefsDefOf.Guilty))
            {
                opinion += 10;
            }

            for (int i = 0; i < ideo.memes.Count; i++)
            {
                MemeDef meme = ideo.memes[i];

                if (!meme.agreeableTraits.NullOrEmpty())
                {
                    for (int j = 0; j < meme.agreeableTraits.Count; j++)
                    {
                        TraitRequirement trait = meme.agreeableTraits[j];

                        if (trait.HasTrait(pawn))
                        {
                            opinion += 10;
                        }
                    }
                }

                if (!meme.disagreeableTraits.NullOrEmpty())
                {
                    for (int j = 0; j < meme.disagreeableTraits.Count; j++)
                    {
                        TraitRequirement trait = meme.disagreeableTraits[j];

                        if (trait.HasTrait(pawn))
                        {
                            opinion -= 10;
                        }
                    }
                }

                if (memeOpinions.ContainsKey(meme))
                {
                    opinion += memeOpinions[meme];
                }
            }

            // -5 opinion per incompatible meme, +5 per shared meme
            opinion -= GameComponent_EnhancedBeliefs.BeliefDifferences(pawnIdeo, ideo) * 5f;
            // Only decrease opinion if we don't like getting converted, shouldn't go the other way
            opinion *= Mathf.Clamp01(pawn.GetStatValue(StatDefOf.CertaintyLossFactor));

            return Mathf.Clamp(opinion, 0, 100);
        }

        public float PersonalIdeoOpinion(Ideo ideo)
        {
            if (!baseIdeoOpinions.ContainsKey(ideo))
            {
                baseIdeoOpinions[ideo] = DefaultIdeoOpinion(ideo);
                personalIdeoOpinions[ideo] = 0;
            }

            float opinion = 0;

            for (int i = 0; i < ideo.memes.Count; i++)
            {
                MemeDef meme = ideo.memes[i];
                if (memeOpinions.ContainsKey(meme))
                {
                    opinion += memeOpinions[meme];
                }
            }

            for (int i = 0; i < ideo.precepts.Count; i++)
            {
                PreceptDef precept = ideo.precepts[i].def;

                if (preceptOpinions.ContainsKey(precept))
                {
                    opinion += preceptOpinions[precept];
                }
            }

            return opinion;
        }

        public float IdeoOpinionFromRelationships(Ideo ideo)
        {
            if (!cachedRelationshipIdeoOpinions.ContainsKey(ideo))
            {
                CacheRelationshipIdeoOpinion(ideo);
            }

            return cachedRelationshipIdeoOpinions[ideo];
        }

        // Calculates ideo opinion offset based on how much pawn likes other pawns of other ideos, should have little weight overall
        // Relationships are a dynamic mess of cosmic scale so there really isn't a better way to do this
        public void RecalculateRelationshipIdeoOpinions()
        {
            List<Ideo> storedIdeos = baseIdeoOpinions.Keys.ToList();

            for (int i = 0; i < baseIdeoOpinions.Count; i++)
            {
                CacheRelationshipIdeoOpinion(storedIdeos[i]);
            }
        }

        // Caches specific ideo opinion from relationships
        public void CacheRelationshipIdeoOpinion(Ideo ideo)
        {
            float opinion = 0;
            GameComponent_EnhancedBeliefs comp = Current.Game.GetComponent<GameComponent_EnhancedBeliefs>();
            List<Pawn> pawns = comp.GetIdeoPawns(ideo);

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn otherPawn = pawns[i];

                // Up to +-2 opinion per pawn
                opinion += pawn.relations.OpinionOf(otherPawn) * 0.02f;
            }

            cachedRelationshipIdeoOpinions[ideo] = opinion;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref lastPositiveThoughtTick, "lastPositiveThoughtTick");
            Scribe_Collections.Look(ref baseIdeoOpinions, "baseIdeoOpinions", LookMode.Reference, LookMode.Value, ref cache1, ref cache5);
            Scribe_Collections.Look(ref personalIdeoOpinions, "personalIdeoOpinions", LookMode.Reference, LookMode.Value, ref cache2, ref cache6);
            Scribe_Collections.Look(ref memeOpinions, "memeOpinions", LookMode.Reference, LookMode.Value, ref cache3, ref cache7);
            Scribe_Collections.Look(ref preceptOpinions, "preceptOpinions", LookMode.Reference, LookMode.Value, ref cache4, ref cache8);
        }

        // Change pawn's personal opinion of another ideo, usually positively
        public void AdjustPersonalOpinion(Ideo ideo, float power)
        {
            if (!baseIdeoOpinions.ContainsKey(ideo))
            {
                baseIdeoOpinions[ideo] = DefaultIdeoOpinion(ideo);
                personalIdeoOpinions[ideo] = 0;
            }

            personalIdeoOpinions[ideo] += power * 100f;
        }

        public void AdjustMemeOpinion(MemeDef meme, float power)
        {
            if (!memeOpinions.ContainsKey(meme))
            {
                memeOpinions[meme] = 0;
            }

            memeOpinions[meme] += power * 100f;
        }

        public void AdjustPreceptOpinion(PreceptDef precept, float power)
        {
            if (!preceptOpinions.ContainsKey(precept))
            {
                preceptOpinions[precept] = 0;
            }

            preceptOpinions[precept] += power * 100f;
        }

        // Check if pawn should get converted to a new ideo after losing certainty in some way.
        public ConversionOutcome CheckConversion(Ideo priorityIdeo = null, bool noBreakdown = false, List<Ideo> excludeIdeos = null, List<Ideo> whitelistIdeos = null)
        {
            if (!ModLister.CheckIdeology("Ideoligion conversion") || pawn.DevelopmentalStage.Baby())
            {
                return ConversionOutcome.Failure;
            }
            if (Find.IdeoManager.classicMode)
            {
                return ConversionOutcome.Failure;
            }

            float certainty = pawn.ideo.Certainty;
            
            if (certainty > 0.2f)
            {
                return ConversionOutcome.Failure;
            }

            float threshold = certainty <= 0f ? 0.6f : 0.85f; //Drastically lower conversion threshold if we're about to have a breakdown
            float currentOpinion = IdeoOpinion(pawn.Ideo);
            List<Ideo> ideos = whitelistIdeos == null ? Find.IdeoManager.IdeosListForReading : whitelistIdeos;
            ideos.SortBy((Ideo x) => IdeoOpinion(x));

            if (excludeIdeos != null)
            {
                ideos = ideos.Except(excludeIdeos).ToList();
            }

            // Moves priority ideo up to the top of the list so if the pawn is being converted and not having a random breakdown, they're gonna probably get converted to the target ideology
            if (priorityIdeo != null)
            {
                ideos.Remove(priorityIdeo);
                ideos.Add(priorityIdeo);
            }

            for (int i = ideos.Count - 1; i >= 0; i--)
            {
                Ideo ideo = ideos[i];

                if (ideo == pawn.Ideo)
                {
                    continue;
                }

                float opinion = IdeoOpinion(ideo);

                // Also don't convert in case we somehow like our current ideo significantly more than the new one
                // Either we have VERY high relationships with a lot of people or very strong personal opinions on current ideology for this to even be possible
                if (opinion < threshold || currentOpinion > opinion)
                {
                    continue;
                }

                // 17% minimal chance of conversion at 20% certrainty and 85% opinion, half that if we're being converted and this is a wrong ideology. Randomly converting to a wrong ideology should be just a rare lol moment
                if (Rand.Value > (1 - certainty * 4f) * (opinion + (certainty <= 0f ? 0.2f : 0)) * (priorityIdeo != null && priorityIdeo != ideo ? 0.5f : 1f))
                {
                    continue;
                }

                Ideo oldIdeo = pawn.Ideo;
                pawn.ideo.SetIdeo(ideo);
                ideo.Notify_MemberGainedByConversion();

                // Move personal opinion into certainty i.e. base opinion, then zero it, since base opinions are fixed and personal beliefs are what is usually meant by certainty anyways
                float[] rundown = DetailedIdeoOpinion(ideo);
                pawn.ideo.Certainty = rundown[0] + rundown[1];
                personalIdeoOpinions[ideo] = 0;
                // Keep current opinion of our old ideo by moving difference between new base and old base (certainty) into personal thoughts
                personalIdeoOpinions[oldIdeo] -= DetailedIdeoOpinion(oldIdeo)[0] - certainty;

                if (!pawn.ideo.PreviousIdeos.Contains(ideo))
                {
                    Find.HistoryEventsManager.RecordEvent(new HistoryEvent(HistoryEventDefOf.ConvertedNewMember, pawn.Named(HistoryEventArgsNames.Doer), ideo.Named(HistoryEventArgsNames.Ideo)));
                }

                return ConversionOutcome.Success;
            }

            if (certainty > 0f || noBreakdown)
            {
                return ConversionOutcome.Failure;
            }

            // Oops
            pawn.mindState.mentalStateHandler.TryStartMentalState(EnhancedBeliefsDefOf.IdeoChange);
            return ConversionOutcome.Breakdown;
        }
    }

    public enum ConversionOutcome : byte
    {
        Failure = 0,
        Breakdown = 1,
        Success = 2
    }
}