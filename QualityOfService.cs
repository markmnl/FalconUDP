using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FalconUDP
{
    /// <summary>
    /// Provides QoS estimates for remote peer.
    /// </summary>
    public class QualityOfService
    {
        private const int ReCalcLatencyAt = 100;

        internal static readonly QualityOfService ZeroedOutQualityOfService = new QualityOfService();

        private readonly double[] roundTripTimes;
        private readonly float resendSampleSizeAsFloat;
        private readonly bool[] wasSendResentSample;

        private bool hasUpdateLatencyBeenCalled;
        private int roundTripTimesIndex;
        private double runningRTTTotal;
        private int updateLatencyCountSinceRecalc;
        private int wasResentIndex;
        private int resentInSampleCount;

        /// <summary>
        /// The current average round trip time from last <see cref="FalconPeer.LatencySampleSize"/> 
        /// reliable messages to till receiving their corresponding ACKnowledgment.
        /// </summary>
        public TimeSpan RoudTripTime { get; private set; }

        /// <summary>
        /// The current number of messages that had to be re-sent per the last <see cref="FalconPeer.ResendRatioSampleSize"/>
        /// reliable sends (as they were not ACKnowledged in time).
        /// </summary>
        public float ResendRatio { get; private set; }

        private QualityOfService()
        { }

        internal QualityOfService(byte latencySampleSize, ushort resendRatioSampleLength)
        {
            this.roundTripTimes = new double[latencySampleSize];
            this.wasSendResentSample = new bool[resendRatioSampleLength];
            this.resendSampleSizeAsFloat = (float)resendRatioSampleLength;
        }

        internal void UpdateLatency(TimeSpan rtt)
        {
            double seconds = rtt.TotalSeconds;

            updateLatencyCountSinceRecalc++;

            // If this is the first time this is being called seed entire sample with inital value
            // and set latency to RTT, it's all we have!
            if (!hasUpdateLatencyBeenCalled)
            {
                for (int i = 0; i < roundTripTimes.Length; i++)
                {
                    roundTripTimes[i] = seconds;
                }
                runningRTTTotal = rtt.TotalSeconds * roundTripTimes.Length;
                RoudTripTime = rtt;
                hasUpdateLatencyBeenCalled = true;
            }
            else
            {
                if (updateLatencyCountSinceRecalc == ReCalcLatencyAt)
                {
                    roundTripTimes[roundTripTimesIndex] = seconds;              // replace oldest RTT in sample with new RTT

                    // Recalc running total from all in sample to remove any drift introduced.
                    runningRTTTotal = 0.0f;
                    foreach (double roundTripTime in roundTripTimes)
                    {
                        runningRTTTotal += roundTripTime;
                    }
                    updateLatencyCountSinceRecalc = 0;
                }
                else
                {
                    runningRTTTotal -= roundTripTimes[roundTripTimesIndex]; // subtract oldest RTT from running total
                    roundTripTimes[roundTripTimesIndex] = seconds;          // replace oldest RTT in sample with new RTT
                    runningRTTTotal += seconds;                             // add new RTT to running total
                }

                RoudTripTime = TimeSpan.FromSeconds(runningRTTTotal / roundTripTimes.Length);     // re-calc average RTT from running total
            }

            // increment index for next time this is called
            roundTripTimesIndex++;
            if (roundTripTimesIndex == roundTripTimes.Length)
            {
                roundTripTimesIndex = 0;
            }
        }

        internal void UpdateResentSample(bool wasResent)
        {
            // update sample
            wasSendResentSample[wasResentIndex] = wasResent;
            wasResentIndex++;
            if (wasResentIndex == wasSendResentSample.Length)
                wasResentIndex = 0;
            
            // update running count of sample that were re-sent
            if (wasResent)
                resentInSampleCount++;

            ResendRatio = resentInSampleCount / resendSampleSizeAsFloat;

            // wasReSentIndex now points to oldest sample remove it from count before replaced 
            // next time this is called.
            if (wasSendResentSample[wasResentIndex])
                resentInSampleCount--;
        }
    }
}
