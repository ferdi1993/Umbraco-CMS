﻿using System;
using Umbraco.Core;
using Umbraco.Core.Models.Membership;

namespace Umbraco.Web.PublishedCache.XmlPublishedCache
{
    class PublishedCachesFactory : PublishedCachesFactoryBase
    {
        private readonly XmlStore _xmlStore;

        public PublishedCachesFactory()
        {
            // instanciate an XmlStore
            // we're not going to have an IXmlStore of some sort, so it's ok to do it here
            // fixme - what-if we instanciate many factories in tests? depends on the store I guess
            // fixme - for tests we'd pass the store to the factory? initialized with an XmlDocument?
            _xmlStore = new XmlStore();
        }

        public override IPublishedCaches CreatePublishedCaches(string previewToken)
        {
            return new PublishedCaches(
                new PublishedContentCache(_xmlStore, previewToken),
                new PublishedMediaCache()); // fixme - search providers
        }

        /// <summary>
        /// Gets the underlying XML store.
        /// </summary>
        public XmlStore XmlStore { get { return _xmlStore; } }

        public override string EnterPreview(IUser user, int contentId)
        {
            var previewContent = new PreviewContent(user.Id, Guid.NewGuid() /*, false*/);
            previewContent.CreatePreviewSet(contentId, true); // preview branch below that content
            return previewContent.Token;
            //previewContent.ActivatePreviewCookie();
        }

        public override void RefreshPreview(string previewToken, int contentId)
        {
            if (previewToken.IsNullOrWhiteSpace()) return;
            var previewContent = new PreviewContent(previewToken);
            previewContent.CreatePreviewSet(contentId, true); // preview branch below that content
        }

        public override void ExitPreview(string previewToken)
        {
            if (previewToken.IsNullOrWhiteSpace()) return;
            var previewContent = new PreviewContent(previewToken);
            previewContent.ClearPreviewSet();
        }
    }
}
