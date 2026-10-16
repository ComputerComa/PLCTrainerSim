using System;
using System.Collections.Generic;

namespace CubeSorter
{
    public enum ReqSrc { Visitor, Auto }
    public enum RS { Queued, Releasing, Rolling, Checking, Detected, Returning, Done }
    public enum Phase { Idle, Sorting, SortDone, Priming, Recirc }

    /// <summary>One requested ball. Requested color (Color) and measured color (Detected) are kept separate: routing always uses the measured one.</summary>
    public class Req
    {
        public int Id, Color;          // Color = pump (requested color) the ball was released from, 1..6
        public int Detected = -1;      // what the color sensor saw: 1..6, 0 = unknown, -1 = not read yet
        public ReqSrc Src; public RS Stage;
    }

    public class HmiMsg { public double Time; public int ReqId; public ReqSrc Src; public string Text; public int Color; }

    /// <summary>
    /// Mock PLC program. One Scan() per PLC scan: reads input tags, writes output tags. It knows nothing about ball objects, only sensors and outputs.
    ///  - Visitor buttons (X_Pend_1..6) fill a bounded queue. A visitor request blocks the automatic stopper, sets that pump's outlet diverter to the visitor path,
    ///    completes one pump cycle, confirms delivery at the visitor gate, then admits the ball to the shared feeder ahead of any further automatic ball.
    ///  - Automatic mode periodically picks a nonempty hopper, keeps its diverter on the automatic path and dispenses (limit and interval configurable).
    ///  - Stage 1: the stopper/gate meters ONE ball onto the colour seat; the single colour sensor reads it. Stage 2: the seat gate passes it to the holding pocket,
    ///    the rotary chute is positioned on the MEASURED color's return tube (or reject), then the pocket gate releases it and the chute must report clear before moving again.
    ///  - Startup: with the 300 balls in the bulk hopper the machine sorts them into the storage hoppers; the operator then starts automatic recirculation.
    /// </summary>
    public class PlcProgram
    {
        // ---- operating parameters (configurable) ----
        public double AutoInterval = 2.5;     // seconds between automatic releases
        public int CircLimit = 8;             // active circulation limit: balls outside the storage hoppers/columns
        public int VisitorQueueMax = 4;       // bounded visitor request queue
        public bool AutoEnabled = true;       // automatic recirculation may dispense
        public double ReadDelay = 0.15;

        // ---- state visible to the HMI / overlay ----
        public Phase Ph = Phase.Idle;
        public bool Running, EStopOk = true;
        public int SortedCount, RejectedCount, VisitorServed, VisitorUnavailable, VisitorRefused, MeasuredMismatch, CircCount, AutoReleased;
        public readonly int[] SortedByColor = new int[7];
        public double Clock;
        public int NextReqId = 1;
        public readonly List<Req> VisitorQ = new List<Req>();
        public Req VisCur;
        public readonly List<HmiMsg> Hmi = new List<HmiMsg>();
        public Req SeatReq, PocketReq;
        public readonly List<Req> Live = new List<Req>();
        public string StatusText = "";

        // ---- internals ----
        int vs, seatSt, pocketSt, admSt;
        double visTimer, admTimer, lastAuto = -1e9;
        bool startPrev, recircPrev; readonly bool[] pendPrev = new bool[6];
        readonly Queue<Req> admitQ = new Queue<Req>();
        readonly int[] jobType = new int[6], jobStage = new int[6]; readonly Req[] jobReq = new Req[6]; readonly bool[] jobFail = new bool[6]; readonly double[] jobTimer = new double[6];
        readonly bool[] primeFail = new bool[6];
        // outputs assembled each scan
        bool oStopper, oVisGate, oSeatGate, oPocketGate, oLight; int oChute;
        readonly bool[] oCycle = new bool[6], oDiv = new bool[6]; readonly int[] oReq = new int[6];
        readonly Random rng;

        public PlcProgram(int seed = 1) { rng = new Random(seed); }

        public bool AnyPumpJob { get { for (int k = 0; k < 6; k++) if (jobType[k] != 0) return true; return false; } }
        public string JobText(int k) { return jobType[k] == 0 ? "-" : (jobType[k] == 1 ? "prime" : jobType[k] == 2 ? "auto" : "visitor") + " s" + jobStage[k]; }
        public string SeqText { get { return "visitor " + vs + "  adm " + admSt + "  seat " + seatSt + "  pocket " + pocketSt; } }

        void Say(Req r, string text, int color = -1)
        {
            Hmi.Add(new HmiMsg { Time = Clock, ReqId = r != null ? r.Id : 0, Src = r != null ? r.Src : ReqSrc.Visitor, Text = text, Color = color >= 0 ? color : (r != null ? r.Color : 0) });
            if (Hmi.Count > 300) Hmi.RemoveRange(0, 150);
        }
        static string Lc(int c) { return Colors.Names[c].ToLowerInvariant(); }
        static string Cap(int c) { return Colors.Names[c]; }

        bool Unavail(TagTable t, int k) { return t.GetBool(Tg.PumpHopEmpty[k]) && !t.GetBool(Tg.PumpCup[k]); }

        // ================= one PLC scan =================
        public void Scan(TagTable t, double dt)
        {
            Clock += dt;
            EStopOk = t.GetBool("X_EStop_NC");
            bool start = t.GetBool("X_Start_PB"), stop = t.GetBool("X_Stop_PB"), recirc = t.GetBool("X_Recirc_PB");
            if (!EStopOk || stop) Running = false;
            else if (start && !startPrev)
            {
                Running = true;
                if (Ph == Phase.Idle) Ph = t.GetInt("DS_Bulk_Count") > 0 ? Phase.Sorting : Phase.Recirc;
            }
            startPrev = start;
            if (recirc && !recircPrev)
            {
                if (Ph == Phase.SortDone || (Ph == Phase.Sorting && t.GetBool("X_Bulk_Empty"))) { Ph = Phase.Priming; Array.Clear(primeFail, 0, 6); AutoEnabled = true; }
                else if (Ph == Phase.Recirc) AutoEnabled = !AutoEnabled;
            }
            recircPrev = recirc;
            bool en = Running && EStopOk;
            t.SetBool("Y_Motion_Enable", en);

            for (int i = 0; i < 6; i++)
            {
                bool b = t.GetBool(Tg.Pend[i]);
                if (b && !pendPrev[i]) OnPendant(t, i + 1);
                pendPrev[i] = b;
            }
            for (int k = 0; k < 6; k++) if (t.GetBool(Tg.PumpReleased[k])) { CircCount++; if (jobType[k] == 2) AutoReleased++; }

            t.SetBool("Y_Pilot_Run", Running); t.SetBool("Y_Pilot_Ready", !Running && EStopOk); t.SetBool("Y_Pilot_Fault", !EStopOk);

            if (en)
            {
                oStopper = false; oVisGate = false; oSeatGate = false; oPocketGate = false; oLight = false;
                PhaseLogic(t);
                VisitorScan(t, dt);
                AutoAdmit(t, dt);
                InspectScan(t, dt);
                PocketScan(t);
                AutoDispatch(t);
                ServicePumps(t, dt);
            }
            else { oStopper = oVisGate = oSeatGate = oPocketGate = oLight = false; for (int k = 0; k < 6; k++) oCycle[k] = false; }

            t.SetBool("Y_AutoStopper_Open", oStopper); t.SetBool("Y_VisGate_Open", oVisGate); t.SetBool("Y_SeatGate_Open", oSeatGate);
            t.SetBool("Y_PocketGate_Open", oPocketGate); t.SetBool("Y_Color_Light", oLight); t.SetInt("DS_Chute_Cmd", oChute);
            for (int k = 0; k < 6; k++) { t.SetBool(Tg.PumpCycle[k], oCycle[k]); t.SetInt(Tg.PumpReqId[k], oReq[k]); t.SetBool(Tg.DivVisitor[k], oDiv[k]); }
            for (int i = Live.Count - 1; i >= 0; i--) if (Live[i].Stage == RS.Done) Live.RemoveAt(i);
        }

        // ---------------- phases ----------------
        void PhaseLogic(TagTable t)
        {
            bool chainEmpty = CircCount <= 0 && !t.GetBool("X_Seat_Ball") && !t.GetBool("X_Pocket_Ball") && !t.GetBool("X_Transfer_Busy") && t.GetBool("X_Feeder_Clear") && !t.GetBool("X_AutoStopper_Ball") && admitQ.Count == 0;
            if (Ph == Phase.Sorting)
            {
                // CircCount only counts pump releases, so in the startup run use the sensors: nothing left upstream of the pocket and the bulk hopper is empty
                bool idle = t.GetBool("X_Bulk_Empty") && !t.GetBool("X_Seat_Ball") && !t.GetBool("X_Pocket_Ball") && !t.GetBool("X_Transfer_Busy") && t.GetBool("X_Feeder_Clear") && !t.GetBool("X_AutoStopper_Ball") && admitQ.Count == 0 && pocketSt == 0 && seatSt == 0;
                if (idle && SortedCount + RejectedCount > 0) { Ph = Phase.SortDone; StatusText = "Sorting complete - start automatic recirculation when ready"; }
            }
            else if (Ph == Phase.Priming)
            {
                bool all = true;
                for (int k = 0; k < 6; k++)
                {
                    if (t.GetBool(Tg.PumpColFull[k])) continue;
                    if (primeFail[k]) continue;
                    all = false;
                    if (jobType[k] == 0) StartJob(k, 1, null);
                }
                if (all && !AnyPumpJob) { Ph = Phase.Recirc; CircCount = 0; lastAuto = Clock; }
            }
            if (Ph == Phase.Sorting) StatusText = "Startup: sorting the bulk hopper into the color hoppers";
            else if (Ph == Phase.Priming) StatusText = "Priming the pump columns";
            else if (Ph == Phase.Recirc) StatusText = AutoEnabled ? "Automatic recirculation" : "Automatic recirculation paused (visitor requests only)";
        }

        // ---------------- visitor input ----------------
        void OnPendant(TagTable t, int color)
        {
            if (!Running) { Say(null, EStopOk ? "The machine is stopped." : "Emergency stop (simulated) - reset it first.", 0); return; }
            if (Ph == Phase.Idle) { return; }
            if (VisitorQ.Count >= VisitorQueueMax) { VisitorRefused++; Say(null, "Queue is full - please wait a moment.", color); return; }
            var r = new Req { Id = NextReqId++, Color = color, Src = ReqSrc.Visitor, Stage = RS.Queued };
            Say(r, Cap(color) + " requested!");
            if (Unavail(t, color - 1) && jobType[color - 1] == 0) { VisitorUnavailable++; Say(r, "Color unavailable-please choose another.".Replace("-", "—")); return; }
            VisitorQ.Add(r); Live.Add(r);
            Say(r, "Your selection is queued.");
        }

        bool BlockAuto(TagTable t) { return VisitorQ.Count > 0 || VisCur != null || t.GetBool("X_VisPath_Ball"); }

        void FailVisitor(Req r)
        {
            VisitorUnavailable++; r.Stage = RS.Done;
            Say(r, "Color unavailable—please choose another.");
        }

        void VisitorScan(TagTable t, double dt)
        {
            switch (vs)
            {
                case 0:
                    if (VisitorQ.Count == 0 || t.GetBool("X_VisPath_Ball")) break;
                    var r = VisitorQ[0]; int k = r.Color - 1;
                    if (!t.GetBool(Tg.PumpColFull[k]))
                    {   // column not primed yet (startup run): prime first, or give up if the hopper has nothing
                        if (jobType[k] == 0) { if (primeFail[k] || Unavail(t, k)) { primeFail[k] = false; VisitorQ.RemoveAt(0); FailVisitor(r); } else StartJob(k, 1, null); }
                        break;
                    }
                    if (jobType[k] != 0) break;      // pump busy with an automatic job: let the movement finish
                    if (Unavail(t, k)) { VisitorQ.RemoveAt(0); FailVisitor(r); break; }
                    VisitorQ.RemoveAt(0); VisCur = r; r.Stage = RS.Releasing; StartJob(k, 3, r); vs = 1;
                    break;
                case 1:
                    {
                        int kk = VisCur.Color - 1;
                        if (jobFail[kk] && jobType[kk] == 0) { jobFail[kk] = false; FailVisitor(VisCur); VisCur = null; vs = 0; break; }
                        if (t.GetBool("X_VisGate_Ball")) { VisCur.Stage = RS.Rolling; vs = 2; }   // delivery confirmed at the visitor gate
                    }
                    break;
                case 2:
                    // admit once the shared feeder and the seat are free (a ball already admitted past the stopper finishes first)
                    if (t.GetBool("X_Feeder_Clear") && !t.GetBool("X_Seat_Ball") && admitQ.Count == 0)
                    { admitQ.Enqueue(VisCur); vs = 3; visTimer = 0; }
                    break;
                case 3:
                    oVisGate = true;
                    if (!t.GetBool("X_VisGate_Ball")) visTimer += dt; else visTimer = 0;
                    if (visTimer > 0.3) vs = 4;
                    break;
                case 4:
                    if (VisCur.Stage >= RS.Checking) { VisCur = null; vs = 0; }
                    break;
            }
        }

        // ---------------- automatic admission at the junction ----------------
        void AutoAdmit(TagTable t, double dt)
        {
            bool allowed = (Ph == Phase.Sorting || Ph == Phase.Recirc || Ph == Phase.Priming);
            if (admSt == 0)
            {
                if (allowed && !BlockAuto(t) && t.GetBool("X_AutoStopper_Ball") && t.GetBool("X_Feeder_Clear") && !t.GetBool("X_Seat_Ball") && admitQ.Count == 0 && vs == 0)
                { admitQ.Enqueue(new Req { Id = 0, Color = 0, Src = ReqSrc.Auto, Stage = RS.Rolling }); admSt = 1; admTimer = 0; }
            }
            else
            {
                oStopper = true; admTimer += dt;
                if (!t.GetBool("X_Feeder_Clear") || admTimer > 3) admSt = 0;   // the admitted ball is past the stopper: close it again at once
            }
        }

        // ---------------- stage 1: seat + colour sensor, then gate to the holding pocket ----------------
        void InspectScan(TagTable t, double dt)
        {
            bool seat = t.GetBool("X_Seat_Ball");
            switch (seatSt)
            {
                case 0:
                    if (seat)
                    {
                        SeatReq = admitQ.Count > 0 ? admitQ.Dequeue() : new Req { Id = 0, Src = ReqSrc.Auto };
                        SeatReq.Stage = RS.Checking; seatSt = 1;
                    }
                    break;
                case 1:
                    oLight = true;
                    if (t.GetBool("X_Color_Valid"))
                    {
                        int c = t.GetInt("DS_Color_Detected"); SeatReq.Detected = c; SeatReq.Stage = RS.Detected;
                        if (c > 0) { Say(SeatReq, "Detected " + Lc(c) + "!", c); if (SeatReq.Src == ReqSrc.Visitor && c != SeatReq.Color) MeasuredMismatch++; }
                        else Say(SeatReq, "Color not recognized—sending the ball to the reject bin.", 0);
                        seatSt = 2;
                    }
                    break;
                case 2:   // stage 2: needs an empty pocket
                    if (!t.GetBool("X_Pocket_Ball") && !t.GetBool("X_Transfer_Busy") && pocketSt == 0) { seatSt = 3; }
                    break;
                case 3:
                    oSeatGate = true;
                    if (!seat) { seatSt = 4; }
                    break;
                case 4:   // close the gate only once the ball has left the seat; wait until it sits in the pocket
                    if (t.GetBool("X_Pocket_Ball") && t.GetBool("X_SeatGate_Closed")) { PocketReq = SeatReq; SeatReq = null; pocketSt = 1; seatSt = 0; }
                    break;
            }
        }

        // ---------------- stage 2: position the rotary chute by the MEASURED color, release, wait for clear ----------------
        void PocketScan(TagTable t)
        {
            switch (pocketSt)
            {
                case 0: break;
                case 1:   // reposition only while the chute reports clear (no ball on the moving arm)
                    if (t.GetBool("X_Chute_Clear")) { int c = PocketReq.Detected; oChute = c > 0 ? c : 7; pocketSt = 2; }
                    break;
                case 2:
                    if (t.GetBool("X_Chute_InPos"))
                    {
                        int c = PocketReq.Detected; PocketReq.Stage = RS.Returning;
                        if (c > 0) Say(PocketReq, "Returning to " + Lc(c) + " storage.", c);
                        pocketSt = 3;
                    }
                    break;
                case 3:
                    oPocketGate = true;
                    if (!t.GetBool("X_Pocket_Ball")) pocketSt = 4;
                    break;
                case 4:
                    oPocketGate = true;
                    if (t.GetBool("X_Chute_Clear")) pocketSt = 5;     // the ball has cleared the arm
                    break;
                case 5:
                    if (t.GetBool("X_PocketGate_Closed"))
                    {
                        int c = PocketReq.Detected;
                        if (c > 0) { SortedCount++; SortedByColor[c]++; } else RejectedCount++;
                        if (PocketReq.Src == ReqSrc.Visitor) VisitorServed++;
                        if (CircCount > 0) CircCount--;
                        PocketReq.Stage = RS.Done; PocketReq = null; pocketSt = 0;
                    }
                    break;
            }
        }

        // ---------------- automatic recirculation ----------------
        void AutoDispatch(TagTable t)
        {
            if (Ph != Phase.Recirc || !AutoEnabled || BlockAuto(t)) return;
            if (Clock - lastAuto < AutoInterval || CircCount >= CircLimit) return;
            for (int k = 0; k < 6; k++) if (jobType[k] == 2) return;    // one automatic stroke at a time
            int start = rng.Next(6);
            for (int i = 0; i < 6; i++)
            {
                int k = (start + i) % 6;
                if (jobType[k] != 0) continue;
                if (t.GetBool(Tg.PumpColFull[k]) && t.GetBool(Tg.PumpCup[k]) && !t.GetBool(Tg.PumpHopEmpty[k]))
                {
                    var r = new Req { Id = 0, Color = k + 1, Src = ReqSrc.Auto, Stage = RS.Releasing };
                    StartJob(k, 2, r); lastAuto = Clock; return;
                }
            }
        }

        // ---------------- pump jobs: diverter first, then one stroke, diverter back only after the ball has cleared ----------------
        void StartJob(int k, int type, Req r) { jobType[k] = type; jobStage[k] = 1; jobReq[k] = r; jobFail[k] = false; jobTimer[k] = 0; }

        void EndJob(int k, bool fail) { jobType[k] = 0; jobStage[k] = 0; oCycle[k] = false; oDiv[k] = false; jobFail[k] = fail; if (fail && jobReq[k] == null) primeFail[k] = true; }

        void ServicePumps(TagTable t, double dt)
        {
            for (int k = 0; k < 6; k++)
            {
                if (jobType[k] == 0) { oCycle[k] = false; oDiv[k] = false; continue; }
                bool vis = jobType[k] == 3;
                jobTimer[k] += dt;
                switch (jobStage[k])
                {
                    case 1:
                        oDiv[k] = vis; oCycle[k] = false;
                        bool atPos = t.GetBool(vis ? Tg.DivAtVis[k] : Tg.DivAtAuto[k]);
                        if (!atPos || t.GetBool(Tg.OutBall[k]) || t.GetBool(Tg.PumpBusy[k]) || t.GetBool(Tg.PumpDone[k])) { if (jobTimer[k] > 8) EndJob(k, true); break; }
                        if (!t.GetBool(Tg.PumpCup[k]))
                        {   // never run an empty pump: give the ramp a moment, then give up
                            if (t.GetBool(Tg.PumpHopEmpty[k]) || jobTimer[k] > 3.5) EndJob(k, true);
                            break;
                        }
                        oReq[k] = jobReq[k] != null ? jobReq[k].Id : 0; oCycle[k] = true; jobStage[k] = 2; jobTimer[k] = 0;
                        if (vis && jobReq[k] != null) Say(jobReq[k], "Releasing your ball\u2026");
                        break;
                    case 2:
                        oDiv[k] = vis; oCycle[k] = true;
                        if (t.GetBool(Tg.PumpFault[k])) { EndJob(k, true); break; }
                        if (t.GetBool(Tg.PumpDone[k])) { oCycle[k] = false; jobStage[k] = 3; jobTimer[k] = 0; }
                        break;
                    case 3:
                        oCycle[k] = false; oDiv[k] = vis;
                        if (t.GetBool(Tg.OutBall[k]) || t.GetBool(Tg.PumpBusy[k])) break;
                        if (jobType[k] == 1) { if (t.GetBool(Tg.PumpColFull[k])) EndJob(k, false); else { jobStage[k] = 1; jobTimer[k] = 0; } }
                        else if (vis) { oDiv[k] = false; jobStage[k] = 4; }   // only now (ball cleared) the flap goes back
                        else EndJob(k, false);
                        break;
                    case 4:
                        oDiv[k] = false;
                        if (t.GetBool(Tg.DivAtAuto[k])) EndJob(k, false);
                        break;
                }
            }
        }
    }

    public class MachineRunner
    {
        public readonly TagTable Tags = new TagTable();
        public readonly PlcProgram Plc;
        public readonly IMachineIO IO;
        public readonly MachineSim Sim;   // null when IO is real hardware
        public double ScanTime = 0.01;
        double acc;

        public MachineRunner(IMachineIO io, int seed = 1)
        {
            IO = io; Sim = io as MachineSim; Plc = new PlcProgram(seed);
            Tags.SetBool("X_EStop_NC", true);
        }

        public void Advance(double dt)
        {
            acc += dt;
            while (acc >= ScanTime)
            {
                acc -= ScanTime;
                IO.ReadInputs(Tags);
                Plc.Scan(Tags, ScanTime);
                IO.WriteOutputs(Tags);
                IO.Step(ScanTime);
            }
        }
    }
}
