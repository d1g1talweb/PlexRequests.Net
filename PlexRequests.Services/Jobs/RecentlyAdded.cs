﻿#region Copyright

// /************************************************************************
//    Copyright (c) 2016 Jamie Rees
//    File: RecentlyAddedModel.cs
//    Created By: Jamie Rees
//   
//    Permission is hereby granted, free of charge, to any person obtaining
//    a copy of this software and associated documentation files (the
//    "Software"), to deal in the Software without restriction, including
//    without limitation the rights to use, copy, modify, merge, publish,
//    distribute, sublicense, and/or sell copies of the Software, and to
//    permit persons to whom the Software is furnished to do so, subject to
//    the following conditions:
//   
//    The above copyright notice and this permission notice shall be
//    included in all copies or substantial portions of the Software.
//   
//    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
//    NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
//    LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
//    OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
//    WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//  ************************************************************************/

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using NLog;
using PlexRequests.Api;
using PlexRequests.Api.Interfaces;
using PlexRequests.Api.Models.Plex;
using PlexRequests.Core;
using PlexRequests.Core.SettingModels;
using PlexRequests.Helpers;
using PlexRequests.Services.Interfaces;
using PlexRequests.Services.Jobs.Templates;
using PlexRequests.Store.Models.Plex;
using Quartz;


namespace PlexRequests.Services.Jobs
{
    public class RecentlyAdded : HtmlTemplateGenerator, IJob, IRecentlyAdded
    {
        public RecentlyAdded(IPlexApi api, ISettingsService<PlexSettings> plexSettings,
            ISettingsService<EmailNotificationSettings> email, IJobRecord rec,
           	ISettingsService<NewletterSettings> newsletter,
            IPlexReadOnlyDatabase db)
        {
            JobRecord = rec;
            Api = api;
            PlexSettings = plexSettings;
            EmailSettings = email;
            NewsletterSettings = newsletter;
            PlexDb = db;
        }

        private IPlexApi Api { get; }
        private TvMazeApi TvApi = new TvMazeApi();
        private readonly TheMovieDbApi _movieApi = new TheMovieDbApi();
        private const int MetadataTypeTv = 4;
        private const int MetadataTypeMovie = 1;
        private ISettingsService<PlexSettings> PlexSettings { get; }
        private ISettingsService<EmailNotificationSettings> EmailSettings { get; }
        private ISettingsService<NewletterSettings> NewsletterSettings { get; }
        private IJobRecord JobRecord { get; }
        private IPlexReadOnlyDatabase PlexDb { get; }

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public void Execute(IJobExecutionContext context)
        {
            try
            {
                var settings = NewsletterSettings.GetSettings();
                if (!settings.SendRecentlyAddedEmail)
                {
                    return;
                }
                
                Start(settings);
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            finally
            {
                JobRecord.Record(JobNames.RecentlyAddedEmail);
            }
        }

        public void Test()
        {
            var settings = NewsletterSettings.GetSettings();
            Start(settings, true);
        }

        private void Start(NewletterSettings newletterSettings, bool testEmail = false)
        {
            var sb = new StringBuilder();
            var plexSettings = PlexSettings.GetSettings();

            var libs = Api.GetLibrarySections(plexSettings.PlexAuthToken, plexSettings.FullUri);
            var tvSection = libs.Directories.FirstOrDefault(x => x.type.Equals(PlexMediaType.Show.ToString(), StringComparison.CurrentCultureIgnoreCase));
            var movieSection = libs.Directories.FirstOrDefault(x => x.type.Equals(PlexMediaType.Movie.ToString(), StringComparison.CurrentCultureIgnoreCase));

            var recentlyAddedTv = Api.RecentlyAdded(plexSettings.PlexAuthToken, plexSettings.FullUri, tvSection.Key);
            var recentlyAddedMovies = Api.RecentlyAdded(plexSettings.PlexAuthToken, plexSettings.FullUri, movieSection.Key);

            GenerateMovieHtml(recentlyAddedMovies, plexSettings, sb);
            GenerateTvHtml(recentlyAddedTv, plexSettings, sb);

            var template = new RecentlyAddedTemplate();
            var html = template.LoadTemplate(sb.ToString());

            Send(newletterSettings, html, plexSettings, testEmail);
        }

        private void GenerateMovieHtml(RecentlyAddedModel movies, PlexSettings plexSettings, StringBuilder sb)
        {
            sb.Append("<h1>New Movies:</h1><br/><br/>");
            sb.Append(
                "<table border=\"0\" cellpadding=\"0\"  align=\"center\" cellspacing=\"0\" style=\"border-collapse: separate; mso-table-lspace: 0pt; mso-table-rspace: 0pt; width: 100%;\" width=\"100%\">");
            foreach (var movie in movies._children.OrderByDescending(x => x.addedAt.UnixTimeStampToDateTime()))
            {
                var plexGUID = string.Empty;
                try
                {
                    var metaData = Api.GetMetadata(plexSettings.PlexAuthToken, plexSettings.FullUri,
                        movie.ratingKey.ToString());

                    plexGUID = metaData.Video.Guid;

                    var imdbId = PlexHelper.GetProviderIdFromPlexGuid(plexGUID);
                    var info = _movieApi.GetMovieInformation(imdbId).Result;

                    AddImageInsideTable(sb, $"https://image.tmdb.org/t/p/w500{info.BackdropPath}");

                    sb.Append("<tr>");
                    sb.Append(
                        "<td align=\"center\" style=\"font-family: sans-serif; font-size: 14px; vertical-align: top;\" valign=\"top\">");

                    Href(sb, $"https://www.imdb.com/title/{info.ImdbId}/");
                    Header(sb, 3, $"{info.Title} {info.ReleaseDate?.ToString("yyyy") ?? string.Empty}");
                    EndTag(sb, "a");

                    if (info.Genres.Any())
                    {
                        AddParagraph(sb,
                            $"Genre: {string.Join(", ", info.Genres.Select(x => x.Name.ToString()).ToArray())}");
                    }

                    AddParagraph(sb, info.Overview);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    Log.Error(
                        "Exception when trying to process a Movie, either in getting the metadata from Plex OR getting the information from TheMovieDB, Plex GUID = {0}",
                        plexGUID);
                }
                finally
                {
                    EndLoopHtml(sb);
                }

            }
            sb.Append("</table><br/><br/>");
        }
        
        private void GenerateTvHtml(RecentlyAddedModel tv, PlexSettings plexSettings, StringBuilder sb)
        {
            // TV
            sb.Append("<h1>New Episodes:</h1><br/><br/>");
            sb.Append(
                "<table border=\"0\" cellpadding=\"0\"  align=\"center\" cellspacing=\"0\" style=\"border-collapse: separate; mso-table-lspace: 0pt; mso-table-rspace: 0pt; width: 100%;\" width=\"100%\">");
            foreach (var t in tv._children.OrderByDescending(x => x.addedAt.UnixTimeStampToDateTime()))
            {
                var plexGUID = string.Empty;
                try
                {

                    var parentMetaData = Api.GetMetadata(plexSettings.PlexAuthToken, plexSettings.FullUri,
                        t.parentRatingKey.ToString());

                    plexGUID = parentMetaData.Directory.Guid;

                    var info = TvApi.ShowLookupByTheTvDbId(int.Parse(PlexHelper.GetProviderIdFromPlexGuid(plexGUID)));

                    var banner = info.image?.original;
                    if (!string.IsNullOrEmpty(banner))
                    {
                        banner = banner.Replace("http", "https"); // Always use the Https banners
                    }
                    AddImageInsideTable(sb, banner);

                    sb.Append("<tr>");
                    sb.Append(
                        "<td align=\"center\" style=\"font-family: sans-serif; font-size: 14px; vertical-align: top;\" valign=\"top\">");

                    var title = $"{t.grandparentTitle} - {t.title}  {t.originallyAvailableAt?.Substring(0, 4)}";

                    Href(sb, $"https://www.imdb.com/title/{info.externals.imdb}/");
                    Header(sb, 3, title);
                    EndTag(sb, "a");

                    AddParagraph(sb, $"Season: {t.parentIndex}, Episode: {t.index}");
                    if (info.genres.Any())
                    {
                        AddParagraph(sb, $"Genre: {string.Join(", ", info.genres.Select(x => x.ToString()).ToArray())}");
                    }

                    AddParagraph(sb, string.IsNullOrEmpty(t.summary) ? info.summary : t.summary);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    Log.Error(
                        "Exception when trying to process a TV Show, either in getting the metadata from Plex OR getting the information from TVMaze, Plex GUID = {0}",
                        plexGUID);
                }
                finally
                {
                    EndLoopHtml(sb);
                }
            }
            sb.Append("</table><br/><br/>");
        }

        private void Send(NewletterSettings newletterSettings, string html, PlexSettings plexSettings, bool testEmail = false)
        {
            var settings = EmailSettings.GetSettings();

            if (!settings.Enabled || string.IsNullOrEmpty(settings.EmailHost))
            {
                return;
            }

            var body = new BodyBuilder { HtmlBody = html, TextBody = "This email is only available on devices that support HTML." };
            var message = new MimeMessage
            {
                Body = body.ToMessageBody(),
                Subject = "New Content on Plex!",
            };

            if (!testEmail)
            {
                if (newletterSettings.SendToPlexUsers)
                {
                    var users = Api.GetUsers(plexSettings.PlexAuthToken);
                    if (users != null)
                    {
                        foreach (var user in users.User)
                        {
                            message.Bcc.Add(new MailboxAddress(user.Username, user.Email));
                        }
                    }
                }

                if (newletterSettings.CustomUsersEmailAddresses != null
                        && newletterSettings.CustomUsersEmailAddresses.Any())
                {
                    foreach (var user in newletterSettings.CustomUsersEmailAddresses)
                    {
                        message.Bcc.Add(new MailboxAddress(user, user));
                    }
                }
            }
            message.Bcc.Add(new MailboxAddress(settings.EmailUsername, settings.RecipientEmail)); // Include the admin

            message.From.Add(new MailboxAddress(settings.EmailUsername, settings.EmailSender));
            try
            {
                using (var client = new SmtpClient())
                {
                    client.Connect(settings.EmailHost, settings.EmailPort); // Let MailKit figure out the correct SecureSocketOptions.

                    // Note: since we don't have an OAuth2 token, disable
                    // the XOAUTH2 authentication mechanism.
                    client.AuthenticationMechanisms.Remove("XOAUTH2");

                    if (settings.Authentication)
                    {
                        client.Authenticate(settings.EmailUsername, settings.EmailPassword);
                    }
                    Log.Info("sending message to {0} \r\n from: {1}\r\n Are we authenticated: {2}", message.To, message.From, client.IsAuthenticated);
                    client.Send(message);
                    client.Disconnect(true);
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private void EndLoopHtml(StringBuilder sb)
        {
            sb.Append("<td");
            sb.Append("<hr>");
            sb.Append("<br>");
            sb.Append("<br>");
            sb.Append("</tr>");
        }

    }
}