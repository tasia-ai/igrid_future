// Compatibility wrappers for grids where ma* functions are provided by the
// TasiaGridFunctionsModule addon instead of being built into the script engine.
// Include these definitions in the script that calls the corresponding functions.

void maGridSay(string name, string message)
{
    modInvokeN("maGridSay", name, message);
}

void maGridSayIM(string name, key agentID, string message)
{
    modInvokeN("maGridSayIM", name, agentID, message);
}

void maChangeGridProfile(string who, string aboutText, key profileImage,
                         string firstLifeText, key firstLifeImage, string webUrl,
                         string accountTitle, string bornOn)
{
    modInvokeN("maChangeGridProfile", who, aboutText, profileImage,
               firstLifeText, firstLifeImage, webUrl, accountTitle, bornOn);
}
