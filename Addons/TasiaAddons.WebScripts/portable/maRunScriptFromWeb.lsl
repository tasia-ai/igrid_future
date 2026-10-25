// Stock grids without the builtin bridge: paste this helper above default {}.
// Do not define this helper on grids which already provide the builtin function.
key maRunScriptFromWeb(string url)
{
    return modInvokeK("maRunScriptFromWeb", url);
}

default
{
    state_entry()
    {
        maRunScriptFromWeb("https://scripts.example.org/hello.json");
    }
    link_message(integer sender, integer code, string message, key request)
    {
        if (code == 202 || code == 403) llOwnerSay(message);
    }
}
