// Touch to play a remote MP3 at 60% volume within 20 metres.
// Errors are reported back to the owner via llOwnerSay.

default
{
    touch_start(integer total_number)
    {
        string url = "https://cdn.cutegrid.net/media/ping.mp3";
        float volume = 0.6;
        float radius = 20.0;
        float cacheHint = 0.0; // set to 1.0 to force a revalidation

        string result = ngcPlaySoundURL(url, volume, radius, cacheHint);
        if (result != "")
        {
            llOwnerSay("ngcPlaySoundURL error: " + result);
        }
        else
        {
            llOwnerSay("Playing: " + url);
        }
    }
}
