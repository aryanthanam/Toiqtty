using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Oroterm.Terminal;

internal static class CellBufferChecks
{
    internal static void Run(Action<bool,string> check)
    {
        int size=Unsafe.SizeOf<TerminalCell>();
        var pool=new TerminalCellPool(32L*size,2,20*size);
        check(size<=40,"Managed cells fit within 40 bytes without native inline UTF-8 storage");
        var first=pool.Rent(8); first[0].Text="private text"; first[0].Background=123;
        pool.Return(first); var reused=pool.Rent(8);
        check(ReferenceEquals(first,reused) && reused.All(cell=>cell.Text is null && cell.Background==0 && cell.Length==0),
            "Reused exact-sized cell buffers release extended text and clear previous appearance");
        var simultaneous=pool.Rent(8);
        check(!ReferenceEquals(reused,simultaneous),"Two live cell leases never share the same array");
        pool.Return(reused); pool.Return(simultaneous); pool.Return(pool.Rent(12)); pool.Return(pool.Rent(20));
        check(pool.Statistics.Bytes<=32L*size && pool.Statistics.Buffers<=2,"Cell-buffer retention obeys both the byte and array-count budgets");
        var oversized=pool.Rent(21); var before=pool.Statistics; pool.Return(oversized);
        check(pool.Statistics.Bytes==before.Bytes && pool.Statistics.Buffers==before.Buffers,"Oversized cell arrays are not retained in the pool");
        var warm=new TerminalCellPool(); var buffer=warm.Rent(128); warm.Return(buffer);
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){buffer=warm.Rent(128);warm.Return(buffer);}
        check(GC.GetAllocatedBytesForCurrentThread()==start,"One thousand warmed exact-size cell rent/return cycles allocate no managed memory");
        var concurrent=new TerminalCellPool(); var live=new ConcurrentDictionary<TerminalCell[],byte>(); int aliases=0;
        Parallel.For(0,8,worker=> {
            for(int i=0;i<500;i++)
            {
                var lease=concurrent.Rent(32+i%7);
                if(!live.TryAdd(lease,0)) Interlocked.Increment(ref aliases);
                lease[0].Text="worker "+worker; Thread.Yield();
                live.TryRemove(lease,out _); concurrent.Return(lease);
            }
        });
        check(aliases==0 && live.IsEmpty,"Concurrent resize/decoder leases preserve exclusive ownership across four thousand reuse cycles");

        using var terminal=new GhosttyTerminal(64,8); terminal.EnableIncrementalRendering();
        string cluster="a"+new string('\u0301',70);
        byte[] referenceText=Encoding.UTF8.GetBytes("\x1b[?2027h"+cluster);
        terminal.Write(referenceText,referenceText.Length);
        var reference=new TerminalCell[512]; terminal.ReadFrame(reference);
        string expectedCluster=reference.Select(cell=>cell.GetText()).MaxBy(text=>text.Length)!;
        check(Encoding.UTF8.GetByteCount(expectedCluster)>128,"The resize corpus includes an upstream grapheme beyond the inline cell-text capacity");
        int nativeAllocations=terminal.NativeCellBufferAllocationsForCheck;
        bool correct=true; var exact=new TerminalCellPool();
        for(int i=0;i<200;i++)
        {
            ushort columns=(ushort)(32+i%33); terminal.Resize(columns,8,9,18);
            var cells=exact.Rent(columns*8);
            string marker=$"BUFFER_{i}";
            byte[] text=Encoding.UTF8.GetBytes("\x1b[?2027h\x1b[2J\x1b[H"+marker+" café 中🙂\r\n"+cluster);
            terminal.Write(text,text.Length); var snapshot=terminal.ReadSnapshot(cells);
            bool frameCorrect=snapshot.Frame.Columns==columns && snapshot.Frame.Rows==8
                && string.Concat(cells.Select(cell=>cell.GetText())).Contains(marker)
                && cells.Any(cell=>cell.GetText()==expectedCluster);
            correct &= frameCorrect;
            exact.Return(cells);
        }
        check(correct,"Repeated resized and recycled buffers preserve current text, Unicode and oversized graphemes");
        check(terminal.NativeCellBufferAllocationsForCheck==nativeAllocations,
            "Two hundred geometry changes within a native capacity bucket reuse the native cell buffer");
        terminal.Resize(1000,40,9,18); terminal.ReadFrame(new TerminalCell[40000]);
        terminal.Resize(64,8,9,18); terminal.ReadFrame(new TerminalCell[512]);
        check(terminal.NativeCellBufferCapacityForCheck==512,"Returning from an unusually large viewport releases the oversized native buffer");
    }
}
