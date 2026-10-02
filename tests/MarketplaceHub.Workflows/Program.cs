using MarketplaceHub.Workflows;
var runner = new WorkflowTestRunner();
var group = args.Length == 2 && args[0] == "--group" ? args[1] : "all";
if (group is "all" or "persistence") await PersistenceTests.Run(runner);
if (group is "all" or "transport") await TransportTests.Run(runner);
if (group is "all" or "purchase") await PurchaseTests.Run(runner);
if (group is "all" or "allocation") await AllocationTests.Run(runner);
if (group is "all" or "coordinator") await CoordinatorTests.Run(runner);
if (group is "all" or "service") await ServiceGateTests.Run(runner);
if (group is not ("all" or "persistence" or "transport" or "purchase" or "allocation" or "coordinator" or "service")) { Console.WriteLine("Unknown group"); return 2; }
Console.WriteLine($"Workflow checks: {runner.Checks}, failures: {runner.Failures}");
return runner.Checks == 0 || runner.Failures != 0 ? 1 : 0;
