using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Model;
using NzbDrone.Core.Repository;
using SubSonic.Repository;

namespace NzbDrone.Core.Providers
{
    public class EpisodeProvider
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly QualityProvider _qualityProvider;
        private readonly SeasonProvider _seasons;
        private readonly SeriesProvider _series;
        private readonly IRepository _sonicRepo;
        private readonly TvDbProvider _tvDb;

        public EpisodeProvider(IRepository sonicRepo, SeriesProvider seriesProvider,
                               SeasonProvider seasonProvider, TvDbProvider tvDbProvider,
                               QualityProvider qualityProvider)
        {
            _sonicRepo = sonicRepo;
            _series = seriesProvider;
            _tvDb = tvDbProvider;
            _seasons = seasonProvider;
            _qualityProvider = qualityProvider;
        }

        public EpisodeProvider()
        {
        }

        public virtual Episode GetEpisode(long id)
        {
            return _sonicRepo.Single<Episode>(id);
        }

        public virtual Episode GetEpisode(int seriesId, int seasonNumber, int episodeNumber)
        {
            return
                _sonicRepo.Single<Episode>(
                    c => c.SeriesId == seriesId && c.SeasonNumber == seasonNumber && c.EpisodeNumber == episodeNumber);
        }

        public virtual IList<Episode> GetEpisodeBySeries(long seriesId)
        {
            return _sonicRepo.Find<Episode>(e => e.SeriesId == seriesId);
        }

        public virtual IList<Episode> GetEpisodeBySeason(long seasonId)
        {
            return _sonicRepo.Find<Episode>(e => e.SeasonId == seasonId);
        }

        public virtual IList<Episode> GetEpisodeByParseResult(EpisodeParseResult parseResult)
        {
            var seasonEpisodes = _sonicRepo.All<Episode>().Where(e =>
                                                   e.SeriesId == parseResult.SeriesId &&
                                                   e.SeasonNumber == parseResult.SeasonNumber).ToList();

            //Has to be done separately since subsonic doesn't support contain method
            return seasonEpisodes.Where(c => parseResult.Episodes.Contains(c.EpisodeNumber)).ToList();

        }


        /// <summary>
        ///   Comprehensive check on whether or not this episode is needed.
        /// </summary>
        /// <param name = "parsedReport">Episode that needs to be checked</param>
        /// <returns></returns>
        public virtual bool IsNeeded(EpisodeParseResult parsedReport)
        {
            foreach (var episode in parsedReport.Episodes)
            {
                var episodeInfo = GetEpisode(parsedReport.SeriesId, parsedReport.SeasonNumber, episode);

                if (episodeInfo == null)
                {
                    Logger.Debug("Episode S{0:00}E{1:00} doesn't exist in db. adding it now.", parsedReport.SeasonNumber, episode);
                    //Todo: How do we want to handle this really? Episode could be released before information is on TheTvDB 
                    //(Parks and Rec did this a lot in the first season, from experience)
                    //Keivan: Should automatically add the episode to db with minimal information. then update the description/title when available.
                    episodeInfo = new Episode
                                      {
                                          SeriesId = parsedReport.SeriesId,
                                          AirDate = DateTime.Now.Date,
                                          EpisodeNumber = episode,
                                          SeasonNumber = parsedReport.SeasonNumber,
                                          Title = String.Empty,
                                          Overview = String.Empty,
                                          Language = "en"
                                      };

                    _sonicRepo.Add(episodeInfo);

                }

                var file = episodeInfo.EpisodeFile;

                if (file != null)
                {
                    Logger.Debug("File is {0} Proper:{1}", file.Quality, file.Proper);

                    //There will never be a time when the episode quality is less than what we have and we want it... ever.... I think.
                    if (file.Quality > parsedReport.Quality)
                    {
                        Logger.Trace("file has better quality. skipping");
                        continue;
                    }

                    //If not null we need to see if this episode has the quality as the download (or if it is better)
                    if (file.Quality == parsedReport.Quality && file.Proper == parsedReport.Proper)
                    {
                        Logger.Trace("Same quality/proper. existing proper. skipping");
                        continue;
                    }

                    //Now we need to handle upgrades and actually pay attention to the Cut-off Value
                    if (file.Quality < parsedReport.Quality)
                    {
                        if (episodeInfo.Series.QualityProfile.Cutoff <= file.Quality)
                        {
                            Logger.Trace("Quality is past cut-off skipping.");
                            continue;
                        }

                    }
                }

                Logger.Debug("Episode {0} is needed", parsedReport);
                return true; //If we get to this point and the file has not yet been rejected then accept it
            }

            Logger.Debug("Episode {0} is not needed", parsedReport);
            return false;
        }

        public virtual void RefreshEpisodeInfo(int seriesId)
        {
            Logger.Info("Starting episode info refresh for series:{0}", seriesId);
            int successCount = 0;
            int failCount = 0;
            var targetSeries = _tvDb.GetSeries(seriesId, true);

            var updateList = new List<Episode>();
            var newList = new List<Episode>();

            Logger.Debug("Updating season info for series:{0}", targetSeries.SeriesName);
            targetSeries.Episodes.Select(e => new { e.SeasonId, e.SeasonNumber })
                .Distinct().ToList()
                .ForEach(s => _seasons.EnsureSeason(seriesId, s.SeasonId, s.SeasonNumber));

            foreach (var episode in targetSeries.Episodes)
            {
                try
                {
                    //DateTime throws an error in SQLServer per message below:
                    //SqlDateTime overflow. Must be between 1/1/1753 12:00:00 AM and 12/31/9999 11:59:59 PM.
                    //So lets hack it so it works for SQLServer (as well as SQLite), perhaps we can find a better solution
                    //Todo: Fix this hack
                    if (episode.FirstAired < new DateTime(1753, 1, 1))
                        episode.FirstAired = new DateTime(1753, 1, 1);

                    Logger.Trace("Updating info for [{0}] - S{1}E{2}", targetSeries.SeriesName, episode.SeasonNumber, episode.EpisodeNumber);
                    var newEpisode = new Episode
                                         {
                                             AirDate = episode.FirstAired,
                                             TvDbEpisodeId = episode.Id,
                                             EpisodeNumber = episode.EpisodeNumber,
                                             Language = episode.Language.Abbriviation,
                                             Overview = episode.Overview,
                                             SeasonId = episode.SeasonId,
                                             SeasonNumber = episode.SeasonNumber,
                                             SeriesId = seriesId,
                                             Title = episode.EpisodeName
                                         };

                    var existingEpisode = GetEpisode(episode.SeriesId, episode.SeasonNumber, episode.EpisodeNumber);

                    if (existingEpisode != null)
                    {
                        newEpisode.EpisodeId = existingEpisode.EpisodeId;
                        updateList.Add(newEpisode);
                    }
                    else
                    {
                        newList.Add(newEpisode);
                    }

                    successCount++;
                }
                catch (Exception e)
                {
                    Logger.FatalException(
                        String.Format("An error has occurred while updating episode info for series {0}", seriesId), e);
                    failCount++;
                }
            }

            _sonicRepo.AddMany(newList);
            _sonicRepo.UpdateMany(updateList);

            Logger.Debug("Finished episode refresh for series:{0}. Successful:{1} - Failed:{2} ",
                         targetSeries.SeriesName, successCount, failCount);
        }

        public virtual void DeleteEpisode(int episodeId)
        {
            _sonicRepo.Delete<Episode>(episodeId);
        }

        public virtual void UpdateEpisode(Episode episode)
        {
            _sonicRepo.Update(episode);
        }
    }
}                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      