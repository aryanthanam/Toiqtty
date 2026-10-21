using System.Text;
using System.Text.Json;
using Toiqtty.Hosting;
using Toiqtty.Terminal;

internal static class SnapshotLimitChecks
{
    internal static void Run(Action<bool,string> check)
    {
        // Individually valid cell strings can exceed the frame budget together.
        string grapheme="a"+new string('\u0301',8191);
        var cells=new TerminalCell[128*64];
        Array.Fill(cells,new TerminalCell {Width=1,Text=grapheme,Length=(uint)Encoding.UTF8.GetByteCount(grapheme)});
        var frame=new TerminalFrame {Columns=128,Rows=64};
        var snapshot=new TerminalSnapshot(frame,[],Enumerable.Range(0,64).ToArray());
        long before=GC.GetAllocatedBytesForCurrentThread();bool rejected=false;
        try{HostSnapshotCodec.Encode(snapshot,cells,[]);}catch(IOException){rejected=true;}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Console.WriteLine("OUTBOUND_LIMIT "+JsonSerializer.Serialize(new {rejected,allocatedBytes=allocated,limitBytes=HostProtocol.MaxSnapshotBytes}));
        check(rejected && allocated<1024*1024,"An oversized outgoing frame is rejected before allocating its transport buffer");

        var baselineCells=new TerminalCell[cells.Length];
        var deltaSnapshot=snapshot with {ChangedRows=[0]};
        byte[] delta=HostSnapshotCodec.EncodeDelta(deltaSnapshot,cells,[]);
        var (Snapshot, Cells) = HostSnapshotCodec.DecodeDelta(delta,[],snapshot,baselineCells);
        check(Cells.Take(128).All(c=>c.GetText()==grapheme) && Cells.Skip(128).All(c=>c.Length==0),
            "Outgoing delta budgeting counts only changed rows while preserving oversized graphemes byte for byte");

        var smallCells=new TerminalCell[4];
        string[] strings=["",new string('a',127),new string('中',128),grapheme];
        for(int i=0;i<strings.Length;i++)smallCells[i]=new() {Text=strings[i],Length=(uint)Encoding.UTF8.GetByteCount(strings[i])};
        var small=new TerminalSnapshot(new() {Columns=4,Rows=1},[],[0]);
        var roundtrip=HostSnapshotCodec.Decode(HostSnapshotCodec.Encode(small,smallCells,[]),[]);
        check(roundtrip.Cells.Select(c=>c.GetText()).SequenceEqual(strings),"Exact outgoing budgets preserve empty, multibyte and multi-byte-prefix text lengths");
    }
}
