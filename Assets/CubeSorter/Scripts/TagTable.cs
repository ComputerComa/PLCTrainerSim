using System.Collections.Generic;

namespace CubeSorter
{
    /// <summary>Mock PLC tag table. Names match Data/plc_tags.csv. Later this is what a Modbus mapping reads/writes.</summary>
    public class TagTable
    {
        readonly Dictionary<string, double> v = new Dictionary<string, double>();
        public bool GetBool(string n) { double d; return v.TryGetValue(n, out d) && d != 0; }
        public void SetBool(string n, bool b) { v[n] = b ? 1 : 0; }
        public int GetInt(string n) { double d; return v.TryGetValue(n, out d) ? (int)d : 0; }
        public void SetInt(string n, int i) { v[n] = i; }
        public double GetFloat(string n) { double d; return v.TryGetValue(n, out d) ? d : 0; }
        public void SetFloat(string n, double f) { v[n] = f; }
        public IEnumerable<KeyValuePair<string, double>> All { get { return v; } }
    }

    /// <summary>The seam between the PLC program and "the machine". Implement with a Modbus client later (MachineSim is the mock).</summary>
    public interface IMachineIO
    {
        /// <summary>Machine sensors/feedback -> Sim->PLC tags.</summary>
        void ReadInputs(TagTable t);
        /// <summary>PLC->Sim tags -> machine outputs.</summary>
        void WriteOutputs(TagTable t);
        /// <summary>Advance the machine model (no-op for real hardware).</summary>
        void Step(double dt);
    }

    /// <summary>Tag names, one place. Index 0..5 = Red, Orange, Blue, Green, White, Black.</summary>
    public static class Tg
    {
        static string[] Make(string f) { var a = new string[6]; for (int i = 0; i < 6; i++) a[i] = string.Format(f, i + 1); return a; }
        // Sim -> PLC
        public static readonly string[] Pend = Make("X_Pend_{0}");
        public static readonly string[] PumpCup = Make("X_Pump{0}_CupBall");
        public static readonly string[] PumpColFull = Make("X_Pump{0}_ColumnFull");
        public static readonly string[] PumpHopEmpty = Make("X_Pump{0}_HopperEmpty");
        public static readonly string[] PumpBusy = Make("X_Pump{0}_Busy");
        public static readonly string[] PumpDone = Make("X_Pump{0}_Done");
        public static readonly string[] PumpReleased = Make("X_Pump{0}_Released");
        public static readonly string[] PumpFault = Make("X_Pump{0}_Fault");
        public static readonly string[] DivAtAuto = Make("X_Pump{0}_DivAuto");
        public static readonly string[] DivAtVis = Make("X_Pump{0}_DivVisitor");
        public static readonly string[] OutBall = Make("X_Pump{0}_OutletBall");
        // PLC -> Sim
        public static readonly string[] PumpCycle = Make("Y_Pump{0}_Cycle");
        public static readonly string[] PumpReqId = Make("DS_Pump{0}_ReqId");
        public static readonly string[] DivVisitor = Make("Y_Pump{0}_DivVisitor");   // 0 = automatic path, 1 = visitor path
    }
}
