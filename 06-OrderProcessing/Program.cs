using OrderProcessingSystem.Phase1;
using OrderProcessingSystem.Phase2;
using OrderProcessingSystem.Phase3;
using OrderProcessingSystem.Phase4;
using OrderProcessingSystem.Shared.Storage;

public static class Program
{
    public static async Task Main(string[] args)
    {
        string[] validModes={"phase1","phase2","phase3","phase4","all"};
        string mode=args.FirstOrDefault(a=>validModes.Contains(a,StringComparer.OrdinalIgnoreCase))??"menu";
        int? cancelAfter=null;
        for(int i=0;i<args.Length;i++)if(args[i]=="--cancel-after"){if(i+1>=args.Length||!int.TryParse(args[i+1],out var n)||n<0){Console.Error.WriteLine("--cancel-after requires a non-negative integer number of milliseconds.");return;}cancelAfter=n;}
        if(mode.Equals("menu",StringComparison.OrdinalIgnoreCase))mode=ReadMenu();
        using var runCts=new CancellationTokenSource();
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;runCts.Cancel();};Console.CancelKeyPress+=handler;
        if(cancelAfter.HasValue)runCts.CancelAfter(cancelAfter.Value);
        var store=new JsonFileStore();var seeder=new DataSeeder(store);
        try
        {
            if(mode.Equals("all",StringComparison.OrdinalIgnoreCase))
            {
                await new Phase1Runner(store,seeder).RunAsync(runCts.Token);if(runCts.IsCancellationRequested)return;Snapshot(store,"phase1");
                await new Phase2Runner(store,seeder).RunAsync(runCts.Token);if(runCts.IsCancellationRequested)return;Snapshot(store,"phase2");
                await new Phase3Runner(store,seeder).RunAsync(runCts.Token);Snapshot(store,"phase3-comparison");if(runCts.IsCancellationRequested)return;
                await new Phase4Runner(store,seeder).RunAsync(runCts.Token);Snapshot(store,"phase4");
            }
            else switch(mode.ToLowerInvariant())
            {
                case "phase1": await new Phase1Runner(store,seeder).RunAsync(runCts.Token);Snapshot(store,"phase1");break;
                case "phase2": await new Phase2Runner(store,seeder).RunAsync(runCts.Token);Snapshot(store,"phase2");break;
                case "phase3": await new Phase3Runner(store,seeder).RunAsync(runCts.Token);Snapshot(store,"phase3-comparison");break;
                case "phase4": await new Phase4Runner(store,seeder).RunAsync(runCts.Token);Snapshot(store,"phase4");break;
                default: Console.WriteLine($"Unknown run mode '{mode}'. Use phase1, phase2, phase3, phase4, or all.");break;
            }
        }
        catch(OperationCanceledException) when(runCts.IsCancellationRequested)
        {
            var rows=await SafeReadOrdersAsync(store,CancellationToken.None);int cancelled=rows.Count(x=>x.Status==OrderProcessingSystem.Shared.Models.OrderStatus.Pending&&x.FailureReason=="Cancelled");int finished=rows.Count-cancelled;Console.WriteLine($"Run cancelled — {finished} orders finished, {cancelled} cancelled");
        }
        catch(Exception ex){Console.Error.WriteLine($"Fatal error: {ex}");Environment.ExitCode=1;}
        finally{Console.CancelKeyPress-=handler;}
    }
    private static string ReadMenu()
    {
        Console.WriteLine("=== E-Commerce Order Processing System ===\n1. Phase 1 — Async Data Access Layer\n2. Phase 2 — Concurrent Processing\n3. Phase 3 — Full Checkout Workflow\n4. Phase 4 — Scale-Up\n5. Run all phases\nChoose 1-5:");return Console.ReadLine()?.Trim() switch{"1"=>"phase1","2"=>"phase2","3"=>"phase3","4"=>"phase4","5"=>"all",_=>"phase1"};
    }
    private static async Task<List<OrderProcessingSystem.Shared.Models.Order>> SafeReadOrdersAsync(JsonFileStore store,CancellationToken ct){try{return await store.ReadAsync<OrderProcessingSystem.Shared.Models.Order>(DataFiles.Orders,ct);}catch{return new();}}
    private static void Snapshot(JsonFileStore store,string phase)
    {
        try{string root=Path.Combine(Environment.CurrentDirectory,"sample-output",phase);if(Directory.Exists(root))Directory.Delete(root,true);CopyDirectory(store.DataDirectory,root);Console.WriteLine($"[Snapshot] final data copied to {root}");}catch(Exception ex){Console.Error.WriteLine($"Snapshot warning: {ex.Message}");}
    }
    private static void CopyDirectory(string source,string target){Directory.CreateDirectory(target);foreach(var file in Directory.GetFiles(source)){var name=Path.GetFileName(file);if(name.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))continue;File.Copy(file,Path.Combine(target,name),true);}foreach(var dir in Directory.GetDirectories(source)){if(Path.GetFileName(dir)=="fireforget-demo")continue;CopyDirectory(dir,Path.Combine(target,Path.GetFileName(dir)));}}
}
