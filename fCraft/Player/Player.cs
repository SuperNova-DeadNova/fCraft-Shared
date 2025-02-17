﻿// Copyright 2009-2014 Matvei Stefarov <me@matvei.org>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using fCraft.Drawing;
using fCraft.Events;
using fCraft.MapGeneration;
using JetBrains.Annotations;

namespace fCraft {
    /// <summary> Represents a callback method for a player-made selection of one or more blocks on a map.
    /// A command may request a number of marks/blocks to select, and a specify callback
    /// to be executed when the desired number of marks/blocks is reached. </summary>
    /// <param name="player"> Player who made the selection. </param>
    /// <param name="marks"> An array of 3D marks/blocks, in terms of block coordinates. </param>
    /// <param name="tag"> An optional argument to pass to the callback,
    /// the value of player.selectionArgs </param>
    public delegate void SelectionCallback( Player player, Vector3I[] marks, object tag );


    /// <summary> Represents the method that responds to a confirmation command. </summary>
    /// <param name="player"> Player who confirmed the action. </param>
    /// <param name="tag"> Parameter that was passed to Player.Confirm() </param>
    /// <param name="fromConsole"> Whether player is console. </param>
    public delegate void ConfirmationCallback( Player player, object tag, bool fromConsole );


    /// <summary> Object representing volatile state ("session") of a connected player.
    /// For persistent state of a known player account, see PlayerInfo. </summary>
    public sealed partial class Player : IClassy {
        internal bool dontmindme = false;
        public static string appName;
        public int extensionCount;
        public List<string> extensions = new List<string>();
        public int customBlockSupportLevel;
        public bool extension;
        public bool loggedIn;
        public static int NTHO_Int(byte[] x, int offset)
        {
            byte[] y = new byte[4];
            Buffer.BlockCopy(x, offset, y, 0, 4); Array.Reverse(y);
            return BitConverter.ToInt32(y, 0);
        }
        public void HandleCustomBlockSupportLevel(byte[] message)
        {
            customBlockSupportLevel = message[0];
        }
        static System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
        static MD5CryptoServiceProvider md5 = new MD5CryptoServiceProvider();
        byte[] HandleMessage(byte[] buffer)
        {
            try
            {
                int length = 0; byte msg = buffer[0];
                // Get the length of the message by checking the first byte
                switch (msg)
                {
                    case 0:
                        length = 130;
                        break; // login
                    case 5:
                        if (!loggedIn)
                            goto default;
                        length = 8;
                        break; // blockchange
                    case 8:
                        if (!loggedIn)
                            goto default;
                        length = 9;
                        break; // input
                    case 13:
                        if (!loggedIn)
                            goto default;
                        length = 65;
                        break; // chat
                    case 16:
                        length = 66;
                        break;
                    case 17:
                        length = 68;
                        break;
                    case 19:
                        length = 1;
                        break;
                    default:
                        if (!dontmindme)
                            Kick("Unhandled message id \"" + msg + "\"!", LeaveReason);
                        else
                            Logger.Log(LogType.Error, Encoding.UTF8.GetString(buffer, 0, buffer.Length)) ;
                        return new byte[0];
                }
                if (buffer.Length > length)
                {
                    byte[] message = new byte[length];
                    Buffer.BlockCopy(buffer, 1, message, 0, length);

                    byte[] tempbuffer = new byte[buffer.Length - length - 1];
                    Buffer.BlockCopy(buffer, length + 1, tempbuffer, 0, buffer.Length - length - 1);

                    buffer = tempbuffer;

                    // Thread thread = null;
                    switch (msg)
                    {
                        case 16:
                            HandleExtInfo(message);
                            break;
                        case 17:
                            HandleExtEntry(message);
                            break;
                        case 19:
                            HandleCustomBlockSupportLevel(message);
                            break;
                    }
                    //thread.Start((object)message);
                    if (buffer.Length > 0)
                        buffer = HandleMessage(buffer);
                    else
                        return new byte[0];
                }
            }
            catch (Exception e)
            {
                Logger.Log(LogType.Error, "ERROR WITH Player.cs");
            }
            return buffer;
        }

        public void HandleExtInfo(byte[] message)
        {
            appName = enc.GetString(message, 0, 64).Trim();
            extensionCount = message[65];
        }
        public struct CPE { public string name; public int version; }
        public List<CPE> ExtEntry = new List<CPE>();
        void HandleExtEntry(byte[] msg)
        {
            AddExtension(enc.GetString(msg, 0, 64).Trim(), NTHO_Int(msg, 64));
            extensionCount--;
        }
        /// <summary> The godly pseudo-player for commands called from the server console.
        /// Console has all the permissions granted.
        /// Note that Player.Console.World is always null,
        /// and that prevents console from calling certain commands (like /TP). </summary>
        public static Player Console, AutoRank;


        #region Properties

        public readonly bool IsSuper;

        /// <summary> Whether the player has completed the login sequence. </summary>
        public SessionState State { get; private set; }

        /// <summary> Whether the player has completed the login sequence. </summary>
        public bool HasRegistered { get; internal set; }

        /// <summary> Whether the player registered and then finished loading the world. </summary>
        public bool HasFullyConnected { get; private set; }

        /// <summary> Whether the client is currently connected. </summary>
        public bool IsOnline {
            get {
                return State == SessionState.Online;
            }
        }

        /// <summary> Whether the player name was verified at login. </summary>
        public bool IsVerified { get; private set; }

        /// <summary> Persistent information record associated with this player. </summary>
        public PlayerInfo Info { get; private set; }

        /// <summary> Whether the player is in paint mode (deleting blocks replaces them). Used by /Paint. </summary>
        public bool IsPainting { get; set; }

        /// <summary> Whether player has blocked all incoming chat.
        /// Deaf players can't hear anything. </summary>
        public bool IsDeaf { get; set; }


        /// <summary> The world that the player is currently on. May be null.
        /// Use .JoinWorld() to make players teleport to another world. </summary>
        [CanBeNull]
        public World World { get; private set; }

        /// <summary> Map from the world that the player is on.
        /// Throws PlayerOpException if player does not have a world.
        /// Loads the map if it's not loaded. Guaranteed to not return null. </summary>
        [NotNull]
        public Map WorldMap {
            get {
                World world = World;
                if( world == null ) PlayerOpException.ThrowNoWorld( this );
                return world.LoadMap();
            }
        }

        /// <summary> Player's position in the current world. </summary>
        public Position Position;


        /// <summary> Time when the session connected. </summary>
        public DateTime LoginTime { get; private set; }

        /// <summary> Last time when the player was active (moving/messaging). UTC. </summary>
        public DateTime LastActiveTime { get; private set; }

        /// <summary> Last time when this player was patrolled by someone. </summary>
        public DateTime LastPatrolTime { get; set; }


        /// <summary> Last command called by the player. </summary>
        [CanBeNull]
        public CommandReader LastCommand { get; private set; }


        /// <summary> Plain version of the name (no formatting). </summary>
        [NotNull]
        public string Name {
            get { return Info.Name; }
        }

        /// <summary> Name formatted for display in the player list. </summary>
        [NotNull]
        public string ListName {
            get {
                string formattedName = Name;
                if( ConfigKey.RankPrefixesInList.Enabled() ) {
                    formattedName = Info.Rank.Prefix + formattedName;
                }
                if( ConfigKey.RankColorsInChat.Enabled() && Info.Rank.Color != Color.White ) {
                    formattedName = Info.Rank.Color + formattedName;
                }
                return formattedName;
            }
        }

        /// <summary> Name formatted for display in chat. </summary>
        public string ClassyName {
            get { return Info.ClassyName; }
        }

        /// <summary> Whether the client supports advanced WoM client functionality. </summary>
        public bool IsUsingWoM { get; private set; }


        /// <summary> Metadata associated with the session/player. </summary>
        [NotNull]
        public MetadataCollection<object> Metadata { get; private set; }

        public MapGeneratorParameters GenParams { get; set; }

        #endregion


        // This constructor is used to create pseudoplayers (such as Console and /dummy).
        // Such players have unlimited permissions, but no world.
        // This should be replaced by a more generic solution, like an IEntity interface.
        internal Player( [NotNull] string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            Info = new PlayerInfo( name, RankManager.HighestRank, true, RankChangeType.AutoPromoted );
            spamBlockLog = new Queue<DateTime>( Info.Rank.AntiGriefBlocks );
            IP = IPAddress.Loopback;
            ResetAllBinds();
            State = SessionState.Offline;
            IsSuper = true;
        }


        #region Chat and Messaging

        static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds( 60 );
        const string WoMAlertPrefix = "^detail.user.alert=";
        int muteWarnings;

        [CanBeNull]
        string partialMessage;

        [CanBeNull]
        internal string lastPrivateMessageSender;


        /// <summary> Parses a message on behalf of this player. </summary>
        /// <param name="rawMessage"> Message to parse. </param>
        /// <param name="fromConsole"> Whether the message originates from console. </param>
        /// <exception cref="ArgumentNullException"> rawMessage is null. </exception>
        public void ParseMessage( [NotNull] string rawMessage, bool fromConsole ) {
            if( rawMessage == null ) throw new ArgumentNullException( "rawMessage" );

            // handle canceling selections and partial messages
            if( rawMessage.StartsWith( "/nvm", StringComparison.OrdinalIgnoreCase ) ||
                rawMessage.StartsWith( "/cancel", StringComparison.OrdinalIgnoreCase ) ) {
                if( partialMessage != null ) {
                    MessageNow( "Partial message cancelled." );
                    partialMessage = null;
                } else if( IsMakingSelection ) {
                    SelectionCancel();
                    MessageNow( "Selection cancelled." );
                } else {
                    MessageNow( "There is currently nothing to cancel." );
                }
                return;
            }

            if( partialMessage != null ) {
                rawMessage = partialMessage + rawMessage;
                partialMessage = null;
            }
            
            // replace %-codes with &-codes
            if( Can( Permission.UseColorCodes ) ) {
                rawMessage = Chat.ReplacePercentColorCodes( rawMessage, true );
            }
            // replace emotes
            if( Can( Permission.UseEmotes ) ) {
                rawMessage = Chat.ReplaceEmoteKeywords( rawMessage );
            }
            rawMessage = Chat.UnescapeBackslashes( rawMessage );

            switch( Chat.GetRawMessageType( rawMessage ) ) {
                case RawMessageType.Chat: {
                        if( !Can( Permission.Chat ) ) return;

                        if( Info.IsMuted ) {
                            MessageMuted();
                            return;
                        }

                        if( DetectChatSpam() ) return;

                        // Escaped slash removed AFTER logging, to avoid confusion with real commands
                        if( rawMessage.StartsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 1 );
                        }

                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }

                        Chat.SendGlobal( this, rawMessage );
                    } break;


                case RawMessageType.Command: {
                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }
                        CommandReader cmd = new CommandReader( rawMessage );
                        CommandDescriptor commandDescriptor = CommandManager.GetDescriptor( cmd.Name, true );

                        if( commandDescriptor == null ) {
                            MessageNow( "Unknown command \"{0}\". See &H/Commands", cmd.Name );
                        } else if( Info.IsFrozen && !commandDescriptor.UsableByFrozenPlayers ) {
                            MessageNow( "&WYou cannot use this command while frozen." );
                        } else {
                            if( !commandDescriptor.DisableLogging ) {
                                Logger.Log( LogType.UserCommand,
                                            "{0}: {1}", Name, rawMessage );
                            }
                            if( commandDescriptor.RepeatableSelection ) {
                                selectionRepeatCommand = cmd;
                            }
                            SendToSpectators( cmd.RawMessage );
                            CommandManager.ParseCommand( this, cmd, fromConsole );
                            if( !commandDescriptor.NotRepeatable ) {
                                LastCommand = cmd;
                            }
                        }
                    } break;


                case RawMessageType.RepeatCommand: {
                        if( LastCommand == null ) {
                            Message( "No command to repeat." );
                        } else {
                            if( Info.IsFrozen && ( LastCommand.Descriptor == null ||
                                                   !LastCommand.Descriptor.UsableByFrozenPlayers ) ) {
                                MessageNow( "&WYou cannot use this command while frozen." );
                                return;
                            }
                            LastCommand.Rewind();
                            Logger.Log( LogType.UserCommand,
                                        "{0} repeated: {1}",
                                        Name, LastCommand.RawMessage );
                            Message( "Repeat: {0}", LastCommand.RawMessage );
                            SendToSpectators( LastCommand.RawMessage );
                            CommandManager.ParseCommand( this, LastCommand, fromConsole );
                        }
                    } break;


                case RawMessageType.PrivateChat: {
                        if( !Can( Permission.Chat ) ) return;

                        if( Info.IsMuted ) {
                            MessageMuted();
                            return;
                        }

                        if( DetectChatSpam() ) return;

                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }

                        string otherPlayerName, messageText;
                        if( rawMessage[1] == ' ' ) {
                            otherPlayerName = rawMessage.Substring( 2, rawMessage.IndexOf( ' ', 2 ) - 2 );
                            messageText = rawMessage.Substring( rawMessage.IndexOf( ' ', 2 ) + 1 );
                        } else {
                            otherPlayerName = rawMessage.Substring( 1, rawMessage.IndexOf( ' ' ) - 1 );
                            messageText = rawMessage.Substring( rawMessage.IndexOf( ' ' ) + 1 );
                        }

                        if( otherPlayerName == "-" ) {
                            if( LastUsedPlayerName != null ) {
                                otherPlayerName = LastUsedPlayerName;
                            } else {
                                Message( "Cannot repeat player name: you haven't used any names yet." );
                                return;
                            }
                        }

                        // first, find ALL players (visible and hidden)
                        Player[] allPlayers = Server.FindPlayers( otherPlayerName, SearchOptions.Default );

                        // if there is more than 1 target player, exclude hidden players
                        if( allPlayers.Length > 1 ) {
                            allPlayers = Server.FindPlayers( this, otherPlayerName, SearchOptions.ReturnSelfIfOnlyMatch );
                        }

                        if( allPlayers.Length == 1 ) {
                            Player target = allPlayers[0];
                            if( target == this ) {
                                Message( "Trying to talk to yourself?" );
                                return;
                            }
                            bool messageSent = false;
                            if( target.CanHear( this ) ) {
                                messageSent = Chat.SendPM( this, target, messageText );
                                SendToSpectators( "to {0}&F: {1}", target.ClassyName, messageText );
                            }

                            if( !CanSee( target ) ) {
                                // message was sent to a hidden player
                                MessageNoPlayer( otherPlayerName );
                                if( messageSent ) {
                                    Info.DecrementMessageWritten();
                                }

                            } else {
                                // message was sent normally
                                LastUsedPlayerName = target.Name;
                                if( target.IsIgnoring( Info ) ) {
                                    if( CanSee( target ) ) {
                                        MessageNow( "&WCannot PM {0}&W: you are ignored.", target.ClassyName );
                                    }
                                } else if( target.IsDeaf ) {
                                    MessageNow( "Cannot PM {0}&S: they are currently deaf.", target.ClassyName );
                                } else {
                                    MessageNow( "&Pto {0}: {1}",
                                                target.Name, messageText );
                                }
                            }

                        } else if( allPlayers.Length == 0 ) {
                            // Cannot PM: target player not found/offline
                            MessageNoPlayer( otherPlayerName );

                        } else {
                            // Cannot PM: more than one player matched
                            MessageManyMatches( "player", allPlayers );
                        }
                    } break;


                case RawMessageType.RankChat: {
                        if( !Can( Permission.Chat ) ) return;

                        if( Info.IsMuted ) {
                            MessageMuted();
                            return;
                        }

                        if( DetectChatSpam() ) return;

                        if( rawMessage.EndsWith( "//" ) ) {
                            rawMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                        }

                        Rank rank;
                        if( rawMessage[2] == ' ' ) {
                            rank = Info.Rank;
                        } else {
                            string rankName = rawMessage.Substring( 2, rawMessage.IndexOf( ' ' ) - 2 );
                            rank = RankManager.FindRank( rankName );
                            if( rank == null ) {
                                MessageNoRank( rankName );
                                break;
                            }
                        }

                        string messageText = rawMessage.Substring( rawMessage.IndexOf( ' ' ) + 1 );

                        Player[] spectators = Server.Players.NotRanked( Info.Rank )
                                                            .Where( p => p.spectatedPlayer == this )
                                                            .ToArray();
                        if( spectators.Length > 0 ) {
                            spectators.Message( "[Spectate]: &Fto rank {0}&F: {1}", rank.ClassyName, messageText );
                        }

                        Chat.SendRank( this, rank, messageText );
                    } break;


                case RawMessageType.Confirmation: {
                        if( Info.IsFrozen ) {
                            MessageNow( "&WYou cannot use any commands while frozen." );
                            return;
                        }
                        if( ConfirmCallback != null ) {
                            if( DateTime.UtcNow.Subtract( ConfirmRequestTime ) < ConfirmationTimeout ) {
                                Logger.Log( LogType.UserCommand, "{0}: /ok", Name );
                                SendToSpectators( "/ok" );
                                ConfirmCallback( this, ConfirmParameter, fromConsole );
                                ConfirmCancel();
                            } else {
                                MessageNow( "Confirmation timed out. Enter the command again." );
                            }
                        } else {
                            MessageNow( "There is no command to confirm." );
                        }
                    } break;


                case RawMessageType.PartialMessage:
                    partialMessage = rawMessage.Substring( 0, rawMessage.Length - 1 );
                    MessageNow( "Partial: &F{0}", partialMessage );
                    break;

                case RawMessageType.Invalid:
                    MessageNow( "Could not parse message." );
                    break;
            }
        }


        /// <summary> Sends a message to all players who are spectating this player, e.g. to forward typed-in commands and PMs.
        /// "System color" code (&amp;S) will be prepended to the message.
        /// If the message does not fit on one line, prefix ">" is prepended to each wrapped line. </summary>
        /// <param name="message"> A composite format string for the message. Same semantics as String.Format(). </param>
        /// <param name="formatArgs"> An object array that contains zero or more objects to format.  </param>
        /// <exception cref="ArgumentNullException"> message or formatArgs is null. </exception>
        /// <exception cref="FormatException"> Message format is invalid. </exception>
        public void SendToSpectators( [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( formatArgs == null ) throw new ArgumentNullException( "formatArgs" );
            Player[] spectators = Server.Players.Where( p => p.spectatedPlayer == this ).ToArray();
            if( spectators.Length > 0 ) {
                spectators.Message( "[Spectate]: &F" + message, formatArgs );
            }
        }


        /// <summary> Sends a message as a WoM alert.
        /// Players who use World of Minecraft client will see this message on the left side of the screen.
        /// Other players will receive it as a normal message. "System color" code (&amp;S) will be prepended to the message. </summary>
        /// <param name="message"> A composite format string for the message. Same semantics as String.Format(). </param>
        /// <param name="formatArgs"> An object array that contains zero or more objects to format. </param>
        /// <exception cref="ArgumentNullException"> message or formatArgs is null. </exception>
        /// <exception cref="FormatException"> Message format is invalid. </exception>
        [StringFormatMethod( "message" )]
        public void MessageWoMAlert( [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( formatArgs == null ) throw new ArgumentNullException( "formatArgs" );
            if( formatArgs.Length > 0 ) {
                message = String.Format( message, formatArgs );
            }
            if( this == Console ) {
                Logger.LogToConsole( message );
            } else if( IsUsingWoM ) {
                foreach( Packet p in LineWrapper.WrapPrefixed( WoMAlertPrefix, WoMAlertPrefix + Color.Sys + message ) ) {
                    Send( p );
                }
            } else {
                foreach( Packet p in LineWrapper.Wrap( Color.Sys + message ) ) {
                    Send( p );
                }
            }
        }


        /// <summary> Sends a text message to this player. "System color" code (&amp;S) will be prepended to the message. 
        /// If the message does not fit on one line, prefix ">" is prepended to each wrapped line. </summary>
        /// <param name="message"> A composite format string for the message. Same semantics as String.Format(). </param>
        /// <param name="formatArgs"> An object array that contains zero or more objects to format. </param>
        /// <exception cref="ArgumentNullException"> message or formatArgs is null. </exception>
        /// <exception cref="FormatException"> Message format is invalid. </exception>
        [StringFormatMethod( "message" )]
        public void Message( [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( formatArgs == null ) throw new ArgumentNullException( "formatArgs" );
            if( formatArgs.Length > 0 ) {
                message = String.Format( message, formatArgs );
            }
            if( IsSuper ) {
                Logger.LogToConsole( message );
            } else {
                foreach( Packet p in LineWrapper.Wrap( Color.Sys + message ) ) {
                    Send( p );
                }
            }
        }


        /// <summary> Sends a text message to this player, prefixing each line. </summary>
        /// <param name="prefix"> Prefix to prepend to prepend to each line after the 1st,
        /// if any line-wrapping occurs. Does NOT get prepended to first line. </param>
        /// <param name="message"> A composite format string for the message. Same semantics as String.Format(). </param>
        /// <param name="formatArgs"> An object array that contains zero or more objects to format. </param>
        /// <exception cref="ArgumentNullException"> prefix, message, or formatArgs is null. </exception>
        /// <exception cref="FormatException"> Message format is invalid. </exception>
        [StringFormatMethod( "message" )]
        public void MessagePrefixed( [NotNull] string prefix, [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( prefix == null ) throw new ArgumentNullException( "prefix" );
            if( message == null ) throw new ArgumentNullException( "message" );
            if( formatArgs == null ) throw new ArgumentNullException( "formatArgs" );
            if( formatArgs.Length > 0 ) {
                message = String.Format( message, formatArgs );
            }
            if( this == Console ) {
                Logger.LogToConsole( message );
            } else {
                foreach( Packet p in LineWrapper.WrapPrefixed( prefix, message ) ) {
                    Send( p );
                }
            }
        }


        [StringFormatMethod( "message" )]
        internal void MessageNow( [NotNull] string message, [NotNull] params object[] args ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args == null ) throw new ArgumentNullException( "args" );
            if( IsDeaf ) return;
            if( args.Length > 0 ) {
                message = String.Format( message, args );
            }
            if( this == Console ) {
                Logger.LogToConsole( message );
            } else {
                if( Thread.CurrentThread != ioThread ) {
                    throw new InvalidOperationException( "SendNow may only be called from player's own thread." );
                }
                foreach( Packet p in LineWrapper.Wrap( Color.Sys + message ) ) {
                    SendNow( p );
                }
            }
        }


        [StringFormatMethod( "message" )]
        internal void MessageNowPrefixed( [NotNull] string prefix, [NotNull] string message, [NotNull] params object[] args ) {
            if( prefix == null ) throw new ArgumentNullException( "prefix" );
            if( message == null ) throw new ArgumentNullException( "message" );
            if( args == null ) throw new ArgumentNullException( "args" );
            if( IsDeaf ) return;
            if( args.Length > 0 ) {
                message = String.Format( message, args );
            }
            if( this == Console ) {
                Logger.LogToConsole( message );
            } else {
                if( Thread.CurrentThread != ioThread ) {
                    throw new InvalidOperationException( "SendNow may only be called from player's own thread." );
                }
                foreach( Packet p in LineWrapper.WrapPrefixed( prefix, message ) ) {
                    Send( p );
                }
            }
        }


        /// <summary> Checks whether this player can hear messages from given sender. 
        /// Deaf and ignoring players will not hear the messages. </summary>
        /// <returns> True if this player will see messages from sender; otherwise false. </returns>
        /// <exception cref="ArgumentNullException"></exception>
        public bool CanHear( [NotNull] Player sender ) {
            if( sender == null ) throw new ArgumentNullException( "sender" );
            return !IsDeaf && !IsIgnoring( sender.Info );
        }

        #region Macros

        /// <summary> Prints "No players found matching ___" message. </summary>
        /// <param name="playerName"> Given name, for which no players were found. </param>
        /// <exception cref="ArgumentNullException"> playerName is null. </exception>
        public void MessageNoPlayer( [NotNull] string playerName ) {
            if( playerName == null ) throw new ArgumentNullException( "playerName" );
            Message( "No players found matching \"{0}\"", playerName );
        }


        /// <summary> Prints "No worlds found matching ___" message. </summary>
        /// <param name="worldName"> Given name, for which no worlds were found. </param>
        /// <exception cref="ArgumentNullException"> worldName is null. </exception>
        public void MessageNoWorld( [NotNull] string worldName ) {
            if( worldName == null ) throw new ArgumentNullException( "worldName" );
            Message( "No worlds found matching \"{0}\". See &H/Worlds", worldName );
        }


        const int MatchesToPrint = int.MaxValue;

        /// <summary> Prints a comma-separated list of matches (up to 30): "More than one ___ matched: ___, ___, ..." </summary>
        /// <param name="itemType"> Type of item in the list. Should be singular (e.g. "player" or "world"). </param>
        /// <param name="items"> List of zero or more matches. ClassyName properties are used in the list. </param>
        /// <exception cref="ArgumentNullException"> itemType or items is null. </exception>
        public void MessageManyMatches( [NotNull] string itemType, [NotNull] IEnumerable<IClassy> items ) {
            if( itemType == null ) throw new ArgumentNullException( "itemType" );
            if( items == null ) throw new ArgumentNullException( "items" );

            IClassy[] itemsEnumerated = items.ToArray();
            string nameList = itemsEnumerated.Take( MatchesToPrint ).JoinToString( ", ", p => p.ClassyName );
            int count = itemsEnumerated.Length;
            if( count > MatchesToPrint ) {
                Message( "More than {0} {1} matched: {2}",
                         count, itemType, nameList );
            } else {
                Message( "More than one {0} matched: {1}",
                         itemType, nameList );
            }
        }


        /// <summary> Prints "This command requires ___+ rank" message. </summary>
        /// <param name="permissions"> List of permissions required for the command. </param>
        /// <exception cref="ArgumentNullException"> permissions is null. </exception>
        /// <exception cref="ArgumentException"> permissions array is empty. </exception>
        public void MessageNoAccess( [NotNull] params Permission[] permissions ) {
            if( permissions == null ) throw new ArgumentNullException( "permissions" );
            if( permissions.Length == 0 ) throw new ArgumentException( "At least one permission required.", "permissions" );
            Rank reqRank = RankManager.GetMinRankWithAllPermissions( permissions );
            if( reqRank == null ) {
                Message( "None of the ranks have permissions for this command." );
            } else {
                Message( "This command requires {0}+&S rank.",
                         reqRank.ClassyName );
            }
        }


        /// <summary> Prints "This command requires ___+ rank" message. </summary>
        /// <param name="cmd"> Command to check. </param>
        /// <exception cref="ArgumentNullException"> cmd is null. </exception>
        public void MessageNoAccess( [NotNull] CommandDescriptor cmd ) {
            if( cmd == null ) throw new ArgumentNullException( "cmd" );
            Rank reqRank = cmd.MinRank;
            if( reqRank == null ) {
                Message( "This command is disabled on the server." );
            } else {
                Message( "This command requires {0}+&S rank.",
                         reqRank.ClassyName );
            }
        }


        /// <summary> Prints "Unrecognized rank ___" message. </summary>
        /// <param name="rankName"> Given name, for which no rank was found. </param>
        public void MessageNoRank( [NotNull] string rankName ) {
            if( rankName == null ) throw new ArgumentNullException( "rankName" );
            Message( "Unrecognized rank \"{0}\". See &H/Ranks", rankName );
        }


        /// <summary> Prints "You cannot access files outside the map folder." message. </summary>
        public void MessageUnsafePath() {
            Message( "&WYou cannot access files outside the map folder." );
        }


        /// <summary> Prints "No zones found matching ___" message. </summary>
        /// <param name="zoneName"> Given name, for which no zones was found. </param>
        public void MessageNoZone( [NotNull] string zoneName ) {
            if( zoneName == null ) throw new ArgumentNullException( "zoneName" );
            Message( "No zones found matching \"{0}\". See &H/Zones", zoneName );
        }


        /// <summary> Prints "Unacceptable world name" message, and requirements for world names. </summary>
        /// <param name="worldName"> Given world name, deemed to be invalid. </param>
        public void MessageInvalidWorldName( [NotNull] string worldName ) {
            if( worldName == null ) throw new ArgumentNullException( "worldName" );
            Message( "Unacceptable world name: \"{0}\"", worldName );
            Message( "World names must be 1-16 characters long, and only contain letters, numbers, and underscores." );
        }


        /// <summary> Prints "___ is not a valid player name" message. </summary>
        /// <param name="playerName"> Given player name, deemed to be invalid. </param>
        public void MessageInvalidPlayerName( [NotNull] string playerName ) {
            if( playerName == null ) throw new ArgumentNullException( "playerName" );
            Message( "\"{0}\" is not a valid player name.", playerName );
        }


        /// <summary> Prints "You are muted for ___ longer" message. </summary>
        public void MessageMuted() {
            Message( "You are muted for {0} longer.",
                     Info.TimeMutedLeft.ToMiniString() );
        }


        /// <summary> Prints "Specify a time range up to ___" message </summary>
        public void MessageMaxTimeSpan() {
            Message( "Specify a time range up to {0}", DateTimeUtil.MaxTimeSpan.ToMiniString() );
        }

        #endregion


        #region Ignore

        readonly HashSet<PlayerInfo> ignoreList = new HashSet<PlayerInfo>();
        readonly object ignoreLock = new object();


        /// <summary> Checks whether this player is currently ignoring a given PlayerInfo.</summary>
        public bool IsIgnoring( [NotNull] PlayerInfo other ) {
            if( other == null ) throw new ArgumentNullException( "other" );
            lock( ignoreLock ) {
                return ignoreList.Contains( other );
            }
        }


        /// <summary> Adds a given PlayerInfo to the ignore list.
        /// Not that ignores are not persistent, and are reset when a player disconnects. </summary>
        /// <param name="other"> Player to ignore. </param>
        /// <returns> True if the player is now ignored,
        /// false is the player has already been ignored previously. </returns>
        public bool Ignore( [NotNull] PlayerInfo other ) {
            if( other == null ) throw new ArgumentNullException( "other" );
            lock( ignoreLock ) {
                if( !ignoreList.Contains( other ) ) {
                    ignoreList.Add( other );
                    return true;
                } else {
                    return false;
                }
            }
        }


        /// <summary> Removes a given PlayerInfo from the ignore list. </summary>
        /// <param name="other"> PlayerInfo to unignore. </param>
        /// <returns> True if the player is no longer ignored,
        /// false if the player was already not ignored. </returns>
        public bool Unignore( [NotNull] PlayerInfo other ) {
            if( other == null ) throw new ArgumentNullException( "other" );
            lock( ignoreLock ) {
                return ignoreList.Remove( other );
            }
        }


        /// <summary> Returns a list of all currently-ignored players. </summary>
        [NotNull]
        public PlayerInfo[] IgnoreList {
            get {
                lock( ignoreLock ) {
                    return ignoreList.ToArray();
                }
            }
        }

        #endregion


        #region Confirmation

        /// <summary> Callback to be called when player types in "/ok" to confirm an action.
        /// Use Player.Confirm(...) methods to set this. </summary>
        [CanBeNull]
        public ConfirmationCallback ConfirmCallback { get; private set; }


        /// <summary> Custom parameter to be passed to Player.ConfirmCallback. </summary>
        [CanBeNull]
        public object ConfirmParameter { get; private set; }


        /// <summary> Time when the confirmation was requested. UTC. </summary>
        public DateTime ConfirmRequestTime { get; private set; }


        static void ConfirmCommandCallback( [NotNull] Player player, object tag, bool fromConsole ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            CommandReader cmd = (CommandReader)tag;
            cmd.Rewind();
            cmd.IsConfirmed = true;
            CommandManager.ParseCommand( player, cmd, fromConsole );
        }


        /// <summary> Request player to confirm continuing with the command.
        /// Player is prompted to type "/ok", and when he/she does,
        /// the command is called again with IsConfirmed flag set. </summary>
        /// <param name="cmd"> Command that needs confirmation. </param>
        /// <param name="message"> Message to print before "Type /ok to continue". </param>
        /// <param name="formatArgs"> Optional String.Format() arguments, for the message. </param>
        /// <exception cref="ArgumentNullException"> cmd, message, or formatArgs is null. </exception>
        [StringFormatMethod( "message" )]
        public void Confirm( [NotNull] CommandReader cmd, [NotNull] string message, [NotNull] params object[] formatArgs ) {
            Confirm( ConfirmCommandCallback, cmd, message, formatArgs );
        }


        /// <summary> Request player to confirm an action.
        /// Player is prompted to type "/ok", and when he/she does, custom callback will be called </summary>
        /// <param name="callback"> Method to call when player confirms. </param>
        /// <param name="callbackParameter"> Argument to pass to the callback. May be null. </param>
        /// <param name="message"> Message to print before "Type /ok to continue". </param>
        /// <param name="formatArgs"> Optional String.Format() arguments, for the message. </param>
        /// <exception cref="ArgumentNullException"> callback, message, or formatArgs is null. </exception>
        [StringFormatMethod( "message" )]
        public void Confirm( [NotNull] ConfirmationCallback callback, [CanBeNull] object callbackParameter,
                             [NotNull] string message, [NotNull] params object[] formatArgs ) {
            if( callback == null ) throw new ArgumentNullException( "callback" );
            if( message == null ) throw new ArgumentNullException( "message" );
            if( formatArgs == null ) throw new ArgumentNullException( "formatArgs" );
            ConfirmCallback = callback;
            ConfirmParameter = callbackParameter;
            ConfirmRequestTime = DateTime.UtcNow;
            Message( "{0} Type &H/ok&S to continue.", String.Format( message, formatArgs ) );
        }


        /// <summary> Cancels any pending confirmation (/ok) prompt. </summary>
        /// <returns> True if a confirmation prompt was pending; otherwise false. </returns>
        public bool ConfirmCancel() {
            if( ConfirmCallback != null ) {
                ConfirmCallback = null;
                ConfirmParameter = null;
                return true;
            } else {
                return false;
            }
        }

        #endregion


        #region AntiSpam

        /// <summary> Number of messages in a AntiSpamInterval seconds required to trigger the anti-spam filter </summary>
        public static int AntispamMessageCount = 3;

        /// <summary> Interval in seconds to record number of message for anti-spam filter </summary>
        public static int AntispamInterval = 4;

        readonly Queue<DateTime> spamChatLog = new Queue<DateTime>( AntispamMessageCount );


        internal bool DetectChatSpam() {
            if( IsSuper || AntispamMessageCount < 1 || AntispamInterval < 1 ) return false;
            if( spamChatLog.Count >= AntispamMessageCount ) {
                DateTime oldestTime = spamChatLog.Dequeue();
                if( DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds < AntispamInterval ) {
                    muteWarnings++;
                    int maxMuteWarnings = ConfigKey.AntispamMaxWarnings.GetInt();
                    if( maxMuteWarnings > 0 && muteWarnings > maxMuteWarnings ) {
                        KickNow( "You were kicked for repeated spamming.", LeaveReason.MessageSpamKick );
                        Server.Message( "&WPlayer {0}&W was kicked for spamming.", ClassyName );
                    } else {
                        TimeSpan autoMuteDuration = TimeSpan.FromSeconds( ConfigKey.AntispamMuteDuration.GetInt() );
                        if( autoMuteDuration > TimeSpan.Zero ) {
                            Info.Mute( Console, autoMuteDuration, false, true );
                            Message( "&WYou have been muted for {0} seconds. Slow down.", autoMuteDuration );
                        } else {
                            Message( "&WYou are sending messages too quickly. Slow down." );
                        }
                    }
                    return true;
                }
            }
            spamChatLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion

        #endregion


        #region Placing Blocks

        // for grief/spam detection
        readonly Queue<DateTime> spamBlockLog = new Queue<DateTime>();

        /// <summary> Last blocktype used by the player.
        /// Make sure to use in conjunction with Player.GetBind() to ensure that bindings are properly applied. </summary>
        public Block LastUsedBlockType { get; private set; }

        /// <summary> Max distance that player may be from a block to reach it (hack detection). </summary>
        public static int MaxBlockPlacementRange { get; set; }


        /// <summary> Handles manually-placed/deleted blocks.
        /// Returns true if player's action should result in a kick. </summary>
        public bool PlaceBlock( Vector3I coord, ClickAction action, Block type ) {
            if( World == null ) PlayerOpException.ThrowNoWorld( this );
            Map map = WorldMap;
            LastUsedBlockType = type;

            Vector3I coordBelow = new Vector3I( coord.X, coord.Y, coord.Z - 1 );

            // check if player is frozen or too far away to legitimately place a block
            if( Info.IsFrozen ||
                Math.Abs( coord.X * 32 - Position.X ) > MaxBlockPlacementRange ||
                Math.Abs( coord.Y * 32 - Position.Y ) > MaxBlockPlacementRange ||
                Math.Abs( coord.Z * 32 - Position.Z ) > MaxBlockPlacementRange ) {
                RevertBlockNow( coord );
                return false;
            }

            if( IsSpectating ) {
                RevertBlockNow( coord );
                Message( "You cannot build or delete while spectating." );
                return false;
            }

            if( World.IsLocked ) {
                RevertBlockNow( coord );
                Message( "This map is currently locked (read-only)." );
                return false;
            }

            if( CheckBlockSpam() ) return true;

            BlockChangeContext context = BlockChangeContext.Manual;
            if( IsPainting && action == ClickAction.Delete ) {
                context |= BlockChangeContext.Replaced;
            }

            // binding and painting
            if( action == ClickAction.Delete && !IsPainting ) {
                type = Block.Air;
            }
            bool requiresUpdate = ( type != GetBind( type ) || IsPainting );
            type = GetBind( type );

            // selection handling
            if( SelectionMarksExpected > 0 && !DisableClickToMark ) {
                RevertBlockNow( coord );
                SelectionAddMark( coord, true, true );
                return false;
            }

            CanPlaceResult canPlaceResult;
            if( type == Block.Slab && coord.Z > 0 && map.GetBlock( coordBelow ) == Block.Slab ) {
                // stair stacking
                canPlaceResult = CanPlace( map, coordBelow, Block.DoubleSlab, context );
            } else {
                // normal placement
                canPlaceResult = CanPlace( map, coord, type, context );
            }

            // if all is well, try placing it
            switch( canPlaceResult ) {
                case CanPlaceResult.Allowed:
                    BlockUpdate blockUpdate;
                    if( type == Block.Slab && coord.Z > 0 && map.GetBlock( coordBelow ) == Block.Slab ) {
                        // handle stair stacking
                        blockUpdate = new BlockUpdate( this, coordBelow, Block.DoubleSlab );
                        Info.ProcessBlockPlaced( (byte)Block.DoubleSlab );
                        map.QueueUpdate( blockUpdate );
                        RaisePlayerPlacedBlockEvent( this, World.Map, coordBelow, Block.Slab, Block.DoubleSlab, context );
                        RevertBlockNow( coord );
                        SendNow( Packet.MakeSetBlock( coordBelow, Block.DoubleSlab ) );

                    } else {
                        // handle normal blocks
                        blockUpdate = new BlockUpdate( this, coord, type );
                        Info.ProcessBlockPlaced( (byte)type );
                        Block old = map.GetBlock( coord );
                        map.QueueUpdate( blockUpdate );
                        RaisePlayerPlacedBlockEvent( this, World.Map, coord, old, type, context );
                        if( requiresUpdate || RelayAllUpdates ) {
                            SendNow( Packet.MakeSetBlock( coord, type ) );
                        }
                    }
                    break;

                case CanPlaceResult.BlocktypeDenied:
                    Message( "&WYou are not permitted to affect this block type." );
                    RevertBlockNow( coord );
                    break;

                case CanPlaceResult.RankDenied:
                    Message( "&WYour rank is not allowed to build." );
                    RevertBlockNow( coord );
                    break;

                case CanPlaceResult.WorldDenied:
                    switch( World.BuildSecurity.CheckDetailed( Info ) ) {
                        case SecurityCheckResult.RankTooLow:
                            Message( "&WYour rank is not allowed to build in this world." );
                            break;
                        case SecurityCheckResult.BlackListed:
                            Message( "&WYou are not allowed to build in this world." );
                            break;
                    }
                    RevertBlockNow( coord );
                    break;

                case CanPlaceResult.ZoneDenied:
                    Zone deniedZone = WorldMap.Zones.FindDenied( coord, this );
                    if( deniedZone != null ) {
                        Message( "&WYou are not allowed to build in zone \"{0}\".", deniedZone.Name );
                    } else {
                        Message( "&WYou are not allowed to build here." );
                    }
                    RevertBlockNow( coord );
                    break;

                case CanPlaceResult.PluginDenied:
                    RevertBlockNow( coord );
                    break;

                //case CanPlaceResult.PluginDeniedNoUpdate:
                //    break;
            }
            return false;
        }


        /// <summary> Sends a block change to THIS PLAYER ONLY. Does not affect the map. </summary>
        /// <param name="coords"> Coordinates of the block. </param>
        /// <param name="block"> Block type to send. </param>
        public void SendBlock( Vector3I coords, Block block ) {
            if( !WorldMap.InBounds( coords ) ) throw new ArgumentOutOfRangeException( "coords" );
            SendLowPriority( Packet.MakeSetBlock( coords, block ) );
        }


        /// <summary> Gets the block from given location in player's world,
        /// and sends it (async) to the player.
        /// Used to undo player's attempted block placement/deletion. </summary>
        public void RevertBlock( Vector3I coords ) {
            SendLowPriority( Packet.MakeSetBlock( coords, WorldMap.GetBlock( coords ) ) );
        }


        // Gets the block from given location in player's world, and sends it (sync) to the player.
        // Used to undo player's attempted block placement/deletion.
        // To avoid threading issues, only use this from this player's IoThread.
        void RevertBlockNow( Vector3I coords ) {
            SendNow( Packet.MakeSetBlock( coords, WorldMap.GetBlock( coords ) ) );
        }


        // returns true if the player is spamming and should be kicked.
        bool CheckBlockSpam() {
            if( Info.Rank.AntiGriefBlocks == 0 || Info.Rank.AntiGriefSeconds == 0 ) return false;
            if( spamBlockLog.Count >= Info.Rank.AntiGriefBlocks ) {
                DateTime oldestTime = spamBlockLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < Info.Rank.AntiGriefSeconds ) {
                    KickNow( "You were kicked by antigrief system. Slow down.", LeaveReason.BlockSpamKick );
                    Server.Message( "{0}&W was kicked for suspected griefing.", ClassyName );
                    Logger.Log( LogType.SuspiciousActivity,
                                "{0} was kicked for block spam ({1} blocks in {2} seconds)",
                                Name, Info.Rank.AntiGriefBlocks, spamTimer );
                    return true;
                }
            }
            spamBlockLog.Enqueue( DateTime.UtcNow );
            return false;
        }

        #endregion


        #region Binding

        readonly Block[] bindings = new Block[256];

        public void Bind( Block type, Block replacement ) {
            bindings[(byte)type] = replacement;
        }

        public void ResetBind( Block type ) {
            bindings[(byte)type] = type;
        }

        public void ResetBind( [NotNull] params Block[] types ) {
            if( types == null ) throw new ArgumentNullException( "types" );
            foreach( Block type in types ) {
                ResetBind( type );
            }
        }

        public Block GetBind( Block type ) {
            return bindings[(byte)type];
        }

        public void ResetAllBinds() {
            foreach( Block block in Enum.GetValues( typeof( Block ) ) ) {
                if( block != Block.None ) {
                    ResetBind( block );
                }
            }
        }

        #endregion


        #region Permission Checks

        /// <summary> Returns true if player has ALL of the given permissions. </summary>
        public bool Can( [NotNull] params Permission[] permissions ) {
            if( permissions == null ) throw new ArgumentNullException( "permissions" );
            return IsSuper || permissions.All( Info.Rank.Can );
        }


        /// <summary> Returns true if player has ANY of the given permissions. </summary>
        public bool CanAny( [NotNull] params Permission[] permissions ) {
            if( permissions == null ) throw new ArgumentNullException( "permissions" );
            return IsSuper || permissions.Any( Info.Rank.Can );
        }


        /// <summary> Returns true if player has the given permission. </summary>
        public bool Can( Permission permission ) {
            return IsSuper || Info.Rank.Can( permission );
        }


        /// <summary> Returns true if player has the given permission,
        /// and is allowed to affect players of the given rank. </summary>
        public bool Can( Permission permission, [NotNull] Rank other ) {
            if( other == null ) throw new ArgumentNullException( "other" );
            return IsSuper || Info.Rank.Can( permission, other );
        }


        /// <summary> Returns true if player is allowed to run
        /// draw commands that affect a given number of blocks. </summary>
        public bool CanDraw( int volume ) {
            if( volume < 0 ) throw new ArgumentOutOfRangeException( "volume" );
            return IsSuper || (Info.Rank.DrawLimit == 0) || (volume <= Info.Rank.DrawLimit);
        }


        /// <summary> Returns true if player is allowed to join a given world. </summary>
        public bool CanJoin( [NotNull] World worldToJoin ) {
            if( worldToJoin == null ) throw new ArgumentNullException( "worldToJoin" );
            return IsSuper || worldToJoin.AccessSecurity.Check( Info );
        }


        /// <summary> Checks whether player is allowed to place a block on the current world at given coordinates.
        /// Raises the PlayerPlacingBlock event. </summary>
        public CanPlaceResult CanPlace( [NotNull] Map map, Vector3I coords, Block newBlock, BlockChangeContext context ) {
            if( map == null ) throw new ArgumentNullException( "map" );
            CanPlaceResult result;

            // check whether coordinate is in bounds
            Block oldBlock = map.GetBlock( coords );
            if( oldBlock == Block.None ) {
                result = CanPlaceResult.OutOfBounds;
                goto eventCheck;
            }

            // check special blocktypes
            if( (newBlock == Block.Admincrete && !Can( Permission.PlaceAdmincrete )) ||
                (newBlock == Block.Water || newBlock == Block.StillWater) && !Can( Permission.PlaceWater ) ||
                (newBlock == Block.Lava || newBlock == Block.StillLava) && !Can( Permission.PlaceLava ) ) {
                result = CanPlaceResult.BlocktypeDenied;
                goto eventCheck;
            }

            // check admincrete-related permissions
            if( oldBlock == Block.Admincrete && !Can( Permission.DeleteAdmincrete ) ) {
                result = CanPlaceResult.BlocktypeDenied;
                goto eventCheck;
            }

            // check zones & world permissions
            PermissionOverride zoneCheckResult = map.Zones.Check( coords, this );
            if( zoneCheckResult == PermissionOverride.Allow ) {
                result = CanPlaceResult.Allowed;
                goto eventCheck;
            } else if( zoneCheckResult == PermissionOverride.Deny ) {
                result = CanPlaceResult.ZoneDenied;
                goto eventCheck;
            }

            // Check world permissions
            World mapWorld = map.World;
            if( mapWorld != null ) {
                switch( mapWorld.BuildSecurity.CheckDetailed( Info ) ) {
                    case SecurityCheckResult.Allowed:
                        // Check world's rank permissions
                        if( (Can( Permission.Build ) || newBlock == Block.Air) &&
                            (Can( Permission.Delete ) || oldBlock == Block.Air) ) {
                            result = CanPlaceResult.Allowed;
                        } else {
                            result = CanPlaceResult.RankDenied;
                        }
                        break;

                    case SecurityCheckResult.WhiteListed:
                        result = CanPlaceResult.Allowed;
                        break;

                    default:
                        result = CanPlaceResult.WorldDenied;
                        break;
                }
            } else {
                result = CanPlaceResult.Allowed;
            }

        eventCheck:
            var handler = PlacingBlock;
            if( handler == null ) return result;

            var e = new PlayerPlacingBlockEventArgs( this, map, coords, oldBlock, newBlock, context, result );
            handler( null, e );
            return e.Result;
        }


        /// <summary> Whether this player can currently see another player as being online.
        /// Players can always see themselves. Super players (e.g. Console) can see all.
        /// Hidden players can only be seen by those of sufficient rank. </summary>
        public bool CanSee( [NotNull] Player other ) {
            if( other == null ) throw new ArgumentNullException( "other" );
            return other == this ||
                   IsSuper ||
                   !other.Info.IsHidden ||
                   Info.Rank.CanSee( other.Info.Rank );
        }


        /// <summary> Whether this player can currently see another player moving.
        /// Behaves very similarly to CanSee method, except when spectating:
        /// Spectators and spectatee cannot see each other.
        /// Spectators can only be seen by those who'd be able to see them hidden. </summary>
        public bool CanSeeMoving( [NotNull] Player otherPlayer ) {
            if( otherPlayer == null ) throw new ArgumentNullException( "otherPlayer" );
            // Check if player can see otherPlayer while they hide/spectate, and whether otherPlayer is spectating player
            bool canSeeOther = (otherPlayer.spectatedPlayer == null && !otherPlayer.Info.IsHidden) ||
                               (otherPlayer.spectatedPlayer != this && Info.Rank.CanSee( otherPlayer.Info.Rank ));

            // Check if player is spectating otherPlayer, or if they're spectating the same target
            bool hideOther = (spectatedPlayer == otherPlayer) ||
                             (spectatedPlayer != null && spectatedPlayer == otherPlayer.spectatedPlayer);

            return otherPlayer == this || // players can always "see" self
                   IsSuper ||             // super-players have ALL permissions
                   canSeeOther && !hideOther;
        }


        /// <summary> Whether this player should see a given world on the /Worlds list by default. </summary>
        public bool CanSee( [NotNull] World world ) {
            if( world == null ) throw new ArgumentNullException( "world" );
            return CanJoin( world ) && !world.IsHidden;
        }

        #endregion


        #region Undo / Redo

        readonly LinkedList<UndoState> undoStack = new LinkedList<UndoState>();
        readonly LinkedList<UndoState> redoStack = new LinkedList<UndoState>();


        [CanBeNull]
        internal UndoState RedoPop() {
            if( redoStack.Count > 0 ) {
                var lastNode = redoStack.Last;
                redoStack.RemoveLast();
                return lastNode.Value;
            } else {
                return null;
            }
        }


        [NotNull]
        internal UndoState RedoBegin( [CanBeNull] DrawOperation op ) {
            LastDrawOp = op;
            UndoState newState = new UndoState( op );
            undoStack.AddLast( newState );
            return newState;
        }


        [NotNull]
        internal UndoState UndoBegin( [CanBeNull] DrawOperation op ) {
            LastDrawOp = op;
            UndoState newState = new UndoState( op );
            redoStack.AddLast( newState );
            return newState;
        }


        [CanBeNull]
        public UndoState UndoPop() {
            if( undoStack.Count > 0 ) {
                var lastNode = undoStack.Last;
                undoStack.RemoveLast();
                return lastNode.Value;
            } else {
                return null;
            }
        }


        [NotNull]
        public UndoState DrawBegin( [CanBeNull] DrawOperation op ) {
            LastDrawOp = op;
            UndoState newState = new UndoState( op );
            undoStack.AddLast( newState );
            if( undoStack.Count > ConfigKey.MaxUndoStates.GetInt() ) {
                undoStack.RemoveFirst();
            }
            redoStack.Clear();
            return newState;
        }

        public void UndoClear() {
            undoStack.Clear();
        }

        public void RedoClear() {
            redoStack.Clear();
        }

        #endregion


        #region Drawing, Selection

        [NotNull]
        public IBrush Brush { get; set; }

        [CanBeNull]
        public DrawOperation LastDrawOp { get; set; }

        /// <summary> Whether clicks should be registered towards selection marks. </summary>
        public bool DisableClickToMark { get; set; }

        /// <summary> Whether player is currently making a selection. </summary>
        public bool IsMakingSelection {
            get { return SelectionMarksExpected > 0; }
        }

        /// <summary> Number of selection marks so far. </summary>
        public int SelectionMarkCount {
            get { return selectionMarks.Count; }
        }

        /// <summary> Number of marks expected to complete the selection. </summary>
        public int SelectionMarksExpected { get; private set; }

        /// <summary> Whether player is repeating a selection (/static) </summary>
        public bool IsRepeatingSelection { get; set; }

        [CanBeNull]
        CommandReader selectionRepeatCommand;

        [CanBeNull]
        SelectionCallback selectionCallback;

        readonly Queue<Vector3I> selectionMarks = new Queue<Vector3I>();

        [CanBeNull]
        object selectionArgs;

        [CanBeNull]
        Permission[] selectionPermissions;


        public void SelectionAddMark( Vector3I pos, bool announce, bool executeCallbackIfNeeded ) {
            if( !IsMakingSelection ) throw new InvalidOperationException( "No selection in progress." );
            selectionMarks.Enqueue( pos );
            if( SelectionMarkCount >= SelectionMarksExpected ) {
                if( executeCallbackIfNeeded ) {
                    SelectionExecute();
                } else if( announce ) {
                    Message( "Last block marked at {0}. Type &H/Mark&S or click any block to continue.", pos );
                }
            } else if( announce ) {
                Message( "Block #{0} marked at {1}. Place mark #{2}.",
                         SelectionMarkCount, pos, SelectionMarkCount + 1 );
            }
        }


        public void SelectionExecute() {
            if( !IsMakingSelection || selectionCallback == null ) {
                throw new InvalidOperationException( "No selection in progress." );
            }
            SelectionMarksExpected = 0;
            // check if player still has the permissions required to complete the selection.
            if( selectionPermissions == null || Can( selectionPermissions ) ) {
                selectionCallback( this, selectionMarks.ToArray(), selectionArgs );
                if( IsRepeatingSelection && selectionRepeatCommand != null ) {
                    selectionRepeatCommand.Rewind();
                    CommandManager.ParseCommand( this, selectionRepeatCommand, this == Console );
                }
                selectionMarks.Clear();
            } else {
                // More complex permission checks can be done in the callback function itself.
                Message( "&WYou are no longer allowed to complete this action." );
                MessageNoAccess( selectionPermissions );
            }
        }


        public void SelectionStart( int marksExpected,
                                    [NotNull] SelectionCallback callback,
                                    [CanBeNull] object args,
                                    [CanBeNull] params Permission[] requiredPermissions ) {
            if( callback == null ) throw new ArgumentNullException( "callback" );
            selectionArgs = args;
            SelectionMarksExpected = marksExpected;
            selectionMarks.Clear();
            selectionCallback = callback;
            selectionPermissions = requiredPermissions;
            if( DisableClickToMark ) {
                Message( "&8Reminder: Click-to-mark is disabled." );
            }
        }


        public void SelectionResetMarks() {
            selectionMarks.Clear();
        }


        public void SelectionCancel() {
            selectionMarks.Clear();
            SelectionMarksExpected = 0;
            selectionCallback = null;
            selectionArgs = null;
            selectionPermissions = null;
        }

        #endregion


        #region Copy/Paste

        /// <summary> Returns a list of all CopyStates, indexed by slot. </summary>
        public CopyState[] CopyStates {
            get { return copyStates; }
        }
        CopyState[] copyStates;

        /// <summary> Gets or sets the currently selected copy slot number. Should be between 0 and (MaxCopySlots-1).
        /// Note that fCraft adds 1 to CopySlot number when presenting it to players.
        /// So 0th slot is shown as "1st" by /CopySlot and related commands; 1st is shown as "2nd", etc. </summary>
        public int CopySlot {
            get { return copySlot; }
            set {
                if( value < 0 || value >= MaxCopySlots) {
                    throw new ArgumentOutOfRangeException( "value" );
                }
                copySlot = value;
            }
        }
        int copySlot;


        /// <summary> Gets or sets the maximum number of copy slots allocated to this player.
        /// Should be nonnegative. CopyStates are preserved when increasing the maximum.
        /// When decreasing the value, any CopyStates in slots that fall outside the new maximum are lost. </summary>
        public int MaxCopySlots {
            get { return copyStates.Length; }
            set {
                if( value < 0 ) throw new ArgumentOutOfRangeException( "value" );
                Array.Resize( ref copyStates, value );
                CopySlot = Math.Min( CopySlot, value - 1 );
            }
        }


        /// <summary> Gets CopyState for currently-selected slot. May be null. </summary>
        /// <returns> CopyState or null, depending on whether anything has been copied into the currently-selected slot. </returns>
        [CanBeNull]
        public CopyState GetCopyState() {
            return GetCopyState( copySlot );
        }


        /// <summary> Gets CopyState for the given slot. May be null. </summary>
        /// <param name="slot"> Slot number. Should be between 0 and (MaxCopySlots-1). </param>
        /// <returns> CopyState or null, depending on whether anything has been copied into the given slot. </returns>
        /// <exception cref="ArgumentOutOfRangeException"> slot is not between 0 and (MaxCopySlots-1). </exception>
        [CanBeNull]
        public CopyState GetCopyState( int slot ) {
            if( slot < 0 || slot >= MaxCopySlots ) {
                throw new ArgumentOutOfRangeException( "slot" );
            }
            return copyStates[slot];
        }


        /// <summary> Stores given CopyState at the currently-selected slot. </summary>
        /// <param name="state"> New content for the current slot. May be a CopyState object, or null. </param>
        /// <returns> Previous contents of the current slot. May be null. </returns>
        [CanBeNull]
        public CopyState SetCopyState( [CanBeNull] CopyState state ) {
            return SetCopyState( state, copySlot );
        }


        /// <summary> Stores given CopyState at the given slot. </summary>
        /// <param name="state"> New content for the given slot. May be a CopyState object, or null. </param>
        /// <param name="slot"> Slot number. Should be between 0 and (MaxCopySlots-1). </param>
        /// <returns> Previous contents of the current slot. May be null. </returns>
        /// <exception cref="ArgumentOutOfRangeException"> slot is not between 0 and (MaxCopySlots-1). </exception>
        [CanBeNull]
        public CopyState SetCopyState( [CanBeNull] CopyState state, int slot ) {
            if( slot < 0 || slot >= MaxCopySlots ) {
                throw new ArgumentOutOfRangeException( "slot" );
            }
            if( state != null ) state.Slot = slot;
            CopyState old = copyStates[slot];
            copyStates[slot] = state;
            return old;
        }

        #endregion


        #region Spectating

        [CanBeNull]
        Player spectatedPlayer;

        /// <summary> Player currently being spectated. Use Spectate/StopSpectate methods to set. </summary>
        [CanBeNull]
        public Player SpectatedPlayer {
            get { return spectatedPlayer; }
        }

        /// <summary> While spectating, currently-spectated player.
        /// When not spectating, most-recently-spectated player. </summary>
        [CanBeNull]
        public PlayerInfo LastSpectatedPlayer { get; private set; }

        readonly object spectateLock = new object();

        /// <summary> Whether this player is currently spectating someone. </summary>
        public bool IsSpectating {
            get { return (spectatedPlayer != null); }
        }


        /// <summary> Starts spectating the given player. </summary>
        /// <param name="target"> Player to spectate. </param>
        /// <returns> True if this player is now spectating the target.
        /// False if this player has already been spectating the target. </returns>
        /// <exception cref="ArgumentNullException"> target is null. </exception>
        /// <exception cref="PlayerOpException"> This player does not have sufficient permissions, or is trying to spectate self. </exception>
        public bool Spectate( [NotNull] Player target ) {
            if( target == null ) throw new ArgumentNullException( "target" );
            lock( spectateLock ) {
                if( spectatedPlayer == target ) return false;

                if( target == this ) {
                    PlayerOpException.ThrowCannotTargetSelf( this, Info, "spectate" );
                }

                if( !Can( Permission.Spectate, target.Info.Rank ) ) {
                    PlayerOpException.ThrowPermissionLimit( this, target.Info, "spectate", Permission.Spectate );
                }

                spectatedPlayer = target;
                LastSpectatedPlayer = target.Info;
                Message( "Now spectating {0}&S. Type &H/unspec&S to stop.", target.ClassyName );
                return true;
            }
        }


        /// <summary> Stops spectating. </summary>
        /// <returns> True if this player was spectating someone (and now stopped).
        /// False if this player was not spectating anyone. </returns>
        public bool StopSpectating() {
            lock( spectateLock ) {
                if( spectatedPlayer == null ) return false;
                Message( "Stopped spectating {0}", spectatedPlayer.ClassyName );
                spectatedPlayer = null;
                return true;
            }
        }

        #endregion


        #region Static Utilities



        static readonly Regex
            EmailRegex = new Regex( @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,6}$", RegexOptions.Compiled ),
            AccountRegex = new Regex( @"^[a-zA-Z0-9._]{2,16}$", RegexOptions.Compiled ),
            PlayerNameRegex = new Regex( @"^([a-zA-Z0-9._]{2,16}|[a-zA-Z0-9._]{1,15}@\d*)$", RegexOptions.Compiled );


        /// <summary> Checks if given string could be an email address.
        /// Matches 99.9% of emails. We don't care about the last 0.1% (and neither does Mojang).
        /// Regex courtesy of http://www.regular-expressions.info/email.html </summary>
        public static bool IsValidEmail( [NotNull] string email ) {
            if( email == null ) throw new ArgumentNullException( "email" );
            return EmailRegex.IsMatch( email );
        }


        /// <summary> Ensures that a player name has the correct length and character set for a Minecraft account.
        /// Does not permit email addresses. </summary>
        public static bool IsValidAccountName( [NotNull] string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            return AccountRegex.IsMatch( name );
        }

        /// <summary> Ensures that a player name has the correct length and character set. </summary>
        public static bool IsValidPlayerName( [NotNull] string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            return PlayerNameRegex.IsMatch( name );
        }
        
        /// <summary> Checks if all characters in a string are admissible in a player name.
        /// Allows '@' (for Mojang accounts) and '.' (for those really old rare accounts). </summary>
        public static bool ContainsValidCharacters( [NotNull] string name ) {
            if( name == null ) throw new ArgumentNullException( "name" );
            for( int i = 0; i < name.Length; i++ ) {
                char ch = name[i];
                if( (ch < '0' && ch != '.') || (ch > '9' && ch < '@') || (ch > 'Z' && ch < '_') || (ch > '_' && ch < 'a') || ch > 'z' ) {
                    return false;
                }
            }
            return true;
        }

        #endregion


        /// <summary> Teleports player to a given coordinate within this map. </summary>
        public void TeleportTo( Position pos ) {
            StopSpectating();
            Send( Packet.MakeSelfTeleport( pos ) );
            Position = pos;
        }


        /// <summary> Time since the player was last active (moved, talked, or clicked). </summary>
        public TimeSpan IdleTime {
            get {
                return DateTime.UtcNow.Subtract( LastActiveTime );
            }
        }


        /// <summary> Resets the IdleTimer to 0. </summary>
        public void ResetIdleTimer() {
            LastActiveTime = DateTime.UtcNow;
        }


        #region Kick

        /// <summary> Advanced kick command. </summary>
        /// <param name="player"> Player who is kicking. </param>
        /// <param name="reason"> Reason for kicking. May be null or blank if allowed by server configuration. </param>
        /// <param name="context"> Classification of kick context. </param>
        /// <param name="announce"> Whether the kick should be announced publicly on the server and IRC. </param>
        /// <param name="raiseEvents"> Whether Player.BeingKicked and Player.Kicked events should be raised. </param>
        /// <param name="recordToPlayerDB"> Whether the kick should be counted towards player's record.</param>
        public void Kick( [NotNull] Player player, [CanBeNull] string reason, LeaveReason context,
                          bool announce, bool raiseEvents, bool recordToPlayerDB ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if( !Enum.IsDefined( typeof( LeaveReason ), context ) ) {
                throw new ArgumentOutOfRangeException( "context" );
            }

            if( reason != null ) reason = reason.Trim( ' ' );
            if( reason != null && reason.Length == 0 ) reason = null;

            // Check if player can ban/unban in general
            if( !player.Can( Permission.Kick ) ) {
                PlayerOpException.ThrowPermissionMissing( player, Info, "kick", Permission.Kick );
            }

            // Check if player is trying to ban/unban self
            if( player == this ) {
                PlayerOpException.ThrowCannotTargetSelf( player, Info, "kick" );
            }

            // Check if player has sufficiently high permission limit
            if( !player.Can( Permission.Kick, Info.Rank ) ) {
                PlayerOpException.ThrowPermissionLimit( player, Info, "kick", Permission.Kick );
            }

            // check if kick reason is missing but required
            PlayerOpException.CheckKickReason( reason, player, Info );

            // raise Player.BeingKicked event
            if( raiseEvents ) {
                var e = new PlayerBeingKickedEventArgs( this, player, reason, announce, recordToPlayerDB, context );
                RaisePlayerBeingKickedEvent( e );
                if( e.Cancel ) PlayerOpException.ThrowCancelled( player, Info );
                recordToPlayerDB = e.RecordToPlayerDB;
            }

            // actually kick
            string kickReason;
            if( reason != null ) {
                kickReason = String.Format( "Kicked by {0}: {1}", player.Name, reason );
            } else {
                kickReason = String.Format( "Kicked by {0}", player.Name );
            }
            Kick( kickReason, context );

            // log and record kick to PlayerDB
            Logger.Log( LogType.UserActivity,
                        "{0} kicked {1}. Reason: {2}",
                        player.Name, Name, reason ?? "" );
            if( recordToPlayerDB ) {
                Info.ProcessKick( player, reason );
            }

            // announce kick
            if( announce ) {
                if( reason != null && ConfigKey.AnnounceKickAndBanReasons.Enabled() ) {
                    Server.Message( "{0}&W was kicked by {1}&W: {2}",
                                    ClassyName, player.ClassyName, reason );
                } else {
                    Server.Message( "{0}&W was kicked by {1}",
                                    ClassyName, player.ClassyName );
                }
            }

            // raise Player.Kicked event
            if( raiseEvents ) {
                var e = new PlayerKickedEventArgs( this, player, reason, announce, recordToPlayerDB, context );
                RaisePlayerKickedEvent( e );
            }
        }

        #endregion


        [CanBeNull]
        public string LastUsedPlayerName { get; set; }

        [CanBeNull]
        public string LastUsedWorldName { get; set; }


        /// <summary> Name formatted for the debugger. </summary>
        public override string ToString() {
            if( Info != null ) {
                return String.Format( "Player({0})", Info.Name );
            } else {
                return String.Format( "Player({0})", IP );
            }
        }
    }


    sealed class PlayerListSorter : IComparer<Player> {
        public static readonly PlayerListSorter Instance = new PlayerListSorter();

        public int Compare( Player x, Player y ) {
            if( x.Info.Rank == y.Info.Rank ) {
                return StringComparer.OrdinalIgnoreCase.Compare( x.Name, y.Name );
            } else {
                return x.Info.Rank.Index - y.Info.Rank.Index;
            }
        }
    }
}