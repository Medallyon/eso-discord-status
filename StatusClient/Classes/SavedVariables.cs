﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NLua;
using NLua.Exceptions;

namespace ESO_Discord_RichPresence_Client
{
    internal class SavedVariables
    {
        public static bool Exists;

        public static string EsoDir =
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Documents\Elder Scrolls Online");

        private readonly FolderBrowserDialog _browser;
        private readonly Discord _client;
        private readonly FileSystemWatcher _watcher;

        internal readonly Main Main;

        public SavedVariables(Main form, Discord client, FolderBrowserDialog browser)
        {
            Main = form;
            _client = client;
            _browser = browser;
            _watcher = new FileSystemWatcher();
        }

        public static string Dir => $@"{EsoDir}\live\SavedVariables";
        public string Path => $@"{Dir}\{Main.ADDON_NAME}.lua";

        public static EsoCharacter ParseLua(string luaTable)
        {
            using (Lua luaClient = new Lua())
            {
                luaClient.State.Encoding = Encoding.UTF8;

                try
                {
                    luaClient.DoString(luaTable);
                }

                catch (LuaException error)
                {
                    DialogResult luaErrorResponse =
                        MessageBox.Show($"Something went wrong while reading data from ESO: {error.Message}", "Error",
                            MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Error, MessageBoxDefaultButton.Button2);

                    if (luaErrorResponse == DialogResult.Retry)
                        ParseLua(luaTable);
                    else if (luaErrorResponse == DialogResult.Abort)
                        Application.Exit();
                }

                LuaTable rootTable = luaClient.GetTable($"{Main.ADDON_NAME}_SavedVars");
                LuaTable defaultTable = (LuaTable) rootTable["Default"];

                var accounts = new Dictionary<object, LuaTable>();
                foreach (object key in defaultTable.Keys)
                {
                    LuaTable value = (LuaTable) defaultTable[key];
                    accounts.Add(key, value);
                }

                return new EsoCharacter((LuaTable) accounts.Values.First()["$AccountWide"]);
            }
        }

        public void Initialise()
        {
            EsoDir = (string) Main.Settings.Get("CustomEsoLocation");

            EnsureSavedVarsExist();
            SetupWatcher();

            if (!Exists)
                return;

            string luaContents = File.ReadAllText(Path);
            Discord.CurrentCharacter = ParseLua(luaContents);
        }

        public void EnsureSavedVarsExist()
        {
            // "Elder Scrolls Online" doesn't exist in "My Documents"
            if (!Directory.Exists(EsoDir))
            {
                Assembly exe = Assembly.GetExecutingAssembly();
                DirectoryInfo cwd = new FileInfo(exe.Location).Directory;

                if (cwd.Name == "Client" && cwd.Parent?.Name == Main.ADDON_NAME && cwd.Parent?.Parent?.Name == "AddOns")
                {
                    EsoDir = cwd.Parent.Parent.Parent.Parent.FullName;
                }
                else
                {
                    DialogResult response =
                        MessageBox.Show(@"Please select the ""Elder Scrolls Online"" folder in your Documents.",
                            "File not found", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation,
                            MessageBoxDefaultButton.Button1);

                    if (response == DialogResult.OK)
                    {
                        if (_browser.ShowDialog() == DialogResult.OK)
                        {
                            EsoDir = _browser.SelectedPath;
                            Main.Settings.Set("CustomEsoLocation", EsoDir);
                        }
                        else
                        {
                            Environment.Exit(1);
                        }
                    }
                    else
                    {
                        Environment.Exit(1);
                    }
                }
            }

            // if LUA file doesn't exist in "SavedVariables"
            if (!File.Exists($@"{Dir}\{Main.ADDON_NAME}.lua"))
            {
                // if ESO addon doesn't exist
                if (!Directory.Exists($@"{EsoDir}\live\AddOns\{Main.ADDON_NAME}"))
                {
                    DialogResult addonResponse = MessageBox.Show(
                        $"The \"{Main.ADDON_NAME}\" AddOn was not detected in your Addons Folder. Do you want to install the addon and try again?",
                        "AddOn Missing", MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation,
                        MessageBoxDefaultButton.Button1);

                    if (addonResponse == DialogResult.OK)
                    {
                        Main.InstallAddon();
                        EnsureSavedVarsExist();
                    }
                    else
                    {
                        Environment.Exit(1);
                    }
                }

                else
                {
                    // Ensure that the AddOn is up-to-date.
                    Main.InstallAddon();

                    Exists = false;
                    Main.UpdateStatusField("Type '/reloadui' into the ESO chat box, then wait.", Color.Goldenrod,
                        FontStyle.Bold);
                }
            }

            else
            {
                Exists = true;
            }
        }

        [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
        private void SetupWatcher()
        {
            try
            {
                _watcher.Path = $@"{Dir}";
                _watcher.Filter = $"{Main.ADDON_NAME}.lua";
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;

                _watcher.Created += OnChanged;
                _watcher.Changed += OnChanged;
                _watcher.Deleted += OnDeleted;
                _watcher.Renamed += OnRenamed;

                _watcher.EnableRaisingEvents = true;
            }

            catch (ArgumentException err)
            {
                Reset();
            }
        }

        public void Reset()
        {
            EsoDir = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Documents\Elder Scrolls Online");
            EnsureSavedVarsExist();
        }

        private void OnChanged(object source, FileSystemEventArgs e)
        {
            Console.WriteLine("SavedVariables file changed or created");
            Exists = true;

            // wait 1 second here to avoid conflicts with file being busy
            Thread.Sleep(1000);

            try
            {
                string luaCharacter = File.ReadAllText(e.FullPath);
                Discord.CurrentCharacter = ParseLua(luaCharacter);
                _client.Enable();
                _client.UpdatePresence(Discord.CurrentCharacter);
            }

            catch (IOException error)
            {
                DialogResult errorResponse =
                    MessageBox.Show($"Something happened while updating your game: {error.Message}", "File Read Error",
                        MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Error, MessageBoxDefaultButton.Button2);

                if (errorResponse == DialogResult.Abort)
                    Environment.Exit(1);
                else if (errorResponse == DialogResult.Retry)
                    OnChanged(source, e);
            }
        }

        private void OnDeleted(object source, FileSystemEventArgs e)
        {
            Console.WriteLine("SavedVariables file deleted");
            Exists = false;
            _client.Disable();

            EnsureSavedVarsExist();
        }

        private void OnRenamed(object source, RenamedEventArgs e)
        {
            Console.WriteLine("SavedVariables file renamed");
            Exists = false;
            _client.Disable();

            EnsureSavedVarsExist();
        }
    }
}