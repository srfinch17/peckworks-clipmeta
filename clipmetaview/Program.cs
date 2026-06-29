using System.Text;
using ClipMetaCore;
using ClipMetaView;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"clipmetaview {ClipMetaVersion.Current}");
    return 0;
}

return await AppRunner.RunAsync(args);
