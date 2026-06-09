namespace BotNexus.Cli;

internal static class CliBanner
{
    internal const string Text = """
░░▒▒▓▓██████████████████████████████████████████████████████████████████▓▓▒▒░░
░▒▓                                                                           
░▒▓  ██████╗  ██████╗ ████████╗███╗   ██╗███████╗██╗  ██╗██╗   ██╗███████╗    
░▒▓  ██╔══██╗██╔═══██╗╚══██╔══╝████╗  ██║██╔════╝╚██╗██╔╝██║   ██║██╔════╝    
░▒▓  ██████╔╝██║   ██║   ██║   ██╔██╗ ██║█████╗   ╚███╔╝ ██║   ██║███████╗    
░▒▓  ██╔══██╗██║   ██║   ██║   ██║╚██╗██║██╔══╝   ██╔██╗ ██║   ██║╚════██║    
░▒▓  ██████╔╝╚██████╔╝   ██║   ██║ ╚████║███████╗██╔╝ ██╗╚██████╔╝███████║    
░▒▓  ╚═════╝  ╚═════╝    ╚═╝   ╚═╝  ╚═══╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝    
░▒▓                                                                           
░▒▓  BotNexus :: LLM ORCHESTRATION LAB :: BAD IDEA DETECTOR :: TOOL WRANGLER  
░▒▓         "Mostly harmless" until someone enables shell access.             
░▒▓                                                                           
░▒▓                ▄▄                   +-- SHELL ACCESS --+                  
░▒▓          ╭────────────╮             |        ON        |                  
░▒▓       ╭──┤   ■    ■   ├──╮          +------------------+                  
░▒▓       │  │            │  │       questionable choices enabled             
░▒▓       ╰──┤   ╰────╯   ├──╯       error budget: lightly smoking            
░▒▓          ╰────────────╯        no body, just terminal confidence          
░▒▓                                   tiny chaos, pocket-sized                
░░▒▒▓▓██████████████████████████████████████████████████████████████████▓▓▒▒░░
""";

    internal static void WriteTo(TextWriter writer)
    {
        writer.WriteLine(Text);
    }
}
