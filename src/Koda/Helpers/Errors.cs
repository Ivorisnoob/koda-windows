using System;
using Koda.Core;

namespace Koda.Helpers;

/// <summary>One voice for every failure the pages show: what went wrong, in a line, and that retry is the way out.</summary>
public static class Errors
{
    public static string Title(Exception error) => error switch
    {
        MusicException { Kind: MusicException.Reason.Network } => "Can't reach YouTube Music",
        MusicException { Kind: MusicException.Reason.RateLimited } => "YouTube asked Koda to slow down",
        MusicException { Kind: MusicException.Reason.BotCheck } => "YouTube refused this session",
        MusicException { Kind: MusicException.Reason.BadResponse } => "This page didn't load",
        _ => "Something went wrong",
    };

    public static string Describe(Exception error) => error switch
    {
        MusicException { Kind: MusicException.Reason.Network } => "Check your connection, then try again.",
        MusicException { Kind: MusicException.Reason.RateLimited } => "Wait a minute, then try again.",
        MusicException { Kind: MusicException.Reason.BotCheck } => "A fresh session usually fixes it. Try again in a moment.",
        MusicException { Kind: MusicException.Reason.Unplayable } m => m.Message,
        MusicException { Kind: MusicException.Reason.BadResponse } => "YouTube answered with something Koda couldn't read. Try again.",
        _ => error.Message,
    };
}
