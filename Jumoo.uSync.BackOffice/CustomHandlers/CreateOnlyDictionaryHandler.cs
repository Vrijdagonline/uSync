using Jumoo.uSync.BackOffice;
using Jumoo.uSync.BackOffice.Handlers;
using Jumoo.uSync.BackOffice.Helpers;
using Jumoo.uSync.Core;
using Jumoo.uSync.Core.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Services;

namespace VrijdagOnline.CustomHandlers
{
    public class CreateOnlyDictionaryHandler : uSyncBaseHandler<IDictionaryItem>, ISyncHandler
    {
        private readonly DictionaryHandler _inner;
        private readonly ILocalizationService _localizationService;
        public string SyncFolder { get { return Constants.Packaging.DictionaryItemNodeName; } }

        public CreateOnlyDictionaryHandler()
        {
            _inner = new DictionaryHandler();
            _localizationService = ApplicationContext.Current.Services.LocalizationService;
        }

        public int Priority { get { return uSyncConstants.Priority.DictionaryItems + 1; } }
        public string Name { get { return "uSync: CreateOnlyDictionaryHandler"; } }

        public IEnumerable<uSyncAction> ExportAll(string folder)
        {
            LogHelper.Info<DictionaryHandler>("Exporting all Dictionary Items");

            var actions = new List<uSyncAction>();

            var _languageService = ApplicationContext.Current.Services.LocalizationService;
            foreach (var item in _languageService.GetRootDictionaryItems())
            {
                if (item != null)
                {
                    actions.Add(ExportToDisk(item, folder));
                }
            }
            return actions;
        }

        public uSyncAction ExportToDisk(IDictionaryItem item, string folder)
        {
            if (item == null)
            {
                return uSyncAction.Fail(Path.GetFileName(folder), typeof(IDictionaryItem), "item not set");
            }

            try
            {
                var attempt = uSyncCoreContext.Instance.DictionarySerializer.Serialize(item);
                var filename = string.Empty;

                if (attempt.Success)
                {
                    filename = uSyncIOHelper.SavePath(folder, SyncFolder, item.ItemKey.ToSafeAlias());
                    uSyncIOHelper.SaveNode(attempt.Item, filename);
                }
                return uSyncActionHelper<XElement>.SetAction(attempt, filename);

            }
            catch (Exception ex)
            {
                return uSyncAction.Fail(item.ItemKey, item.GetType(), ChangeType.Export, ex);
            }
        }

        public override SyncAttempt<IDictionaryItem> Import(string filePath, bool force = false)
        {
            var languages = _localizationService.GetAllLanguages();
            var node = XElement.Load(filePath);
            var rootItem = default(IDictionaryItem);

            var didRootItemImport = TryImportItem(node, languages, out rootItem);
            var didChildrenImport = TryImportChildren(node, rootItem, languages);

            var didAnythingChange = didRootItemImport || didChildrenImport;
            var changeType = didAnythingChange ? ChangeType.Import : ChangeType.NoChange;

            return SyncAttempt<IDictionaryItem>.Succeed(rootItem.ItemKey, rootItem, changeType);
        }

        private bool TryImportItem(XElement node, IEnumerable<ILanguage> languages, out IDictionaryItem dictionaryItem, Guid? parent = null)
        {
            var wasImported = false;
            var itemKey = node.NameFromNode();

            if (_localizationService.DictionaryItemExists(itemKey))
            {
                dictionaryItem = _localizationService.GetDictionaryItemByKey(itemKey);
            }
            else
            {
                dictionaryItem = _localizationService.CreateDictionaryItemWithIdentity(itemKey, parent);

                foreach (var valueNode in node.Elements("Value"))
                {
                    var languageId = valueNode.Attribute("LanguageCultureAlias").Value;
                    var language = languages.FirstOrDefault(x => x.IsoCode == languageId);

                    if (language != null)
                    {
                        _localizationService.AddOrUpdateDictionaryValue(dictionaryItem, language, valueNode.Value);
                    }
                }

                _localizationService.Save(dictionaryItem);

                wasImported = true;
            }
            return wasImported;
        }
        private bool TryImportChildren(XElement node, IDictionaryItem rootItem, IEnumerable<ILanguage> languages)
        {
            var children = node.Elements("DictionaryItem");
            var haveAnyChildrenImported = false;
            var haveAnyChildrensChildrenImported = false;

            foreach (var child in children)
            {
                var childDictionaryItem = default(IDictionaryItem);

                var didChildImport = TryImportItem(child, languages, out childDictionaryItem, rootItem.Key);
                var didChildsChildrenImport = TryImportChildren(child, childDictionaryItem, languages);

                if (haveAnyChildrenImported == false)
                {
                    haveAnyChildrenImported = didChildImport;
                }

                if (haveAnyChildrensChildrenImported)
                {
                    haveAnyChildrensChildrenImported = didChildsChildrenImport;
                }
            }

            return haveAnyChildrenImported || haveAnyChildrensChildrenImported;
        }

        public override uSyncAction ReportItem(string file)
        {
            var node = XElement.Load(file);
            var update = uSyncCoreContext.Instance.DictionarySerializer.IsUpdate(node);
            var action = uSyncActionHelper<IDictionaryItem>.ReportAction(update, node.NameFromNode(), "Dictionary Items often get their order mixed up");

            return action;
        }

        public void RegisterEvents()
        {
            LocalizationService.SavedDictionaryItem += LocalizationService_SavedDictionaryItem;
            LocalizationService.DeletingDictionaryItem += LocalizationService_DeletingDictionaryItem;
            LocalizationService.DeletedDictionaryItem += LocalizationService_DeletedDictionaryItem;

            _deleteSaves = new List<string>();
        }

        private void LocalizationService_DeletedDictionaryItem(ILocalizationService sender, Umbraco.Core.Events.DeleteEventArgs<IDictionaryItem> e)
        {
            foreach (var save in _deleteSaves)
            {
                LogHelper.Info<DictionaryHandler>("Saveing top after delete");

                var item = ApplicationContext.Current.Services.LocalizationService.GetDictionaryItemByKey(save);
                if (item != null)
                    ExportToDisk(item, uSyncBackOfficeContext.Instance.Configuration.Settings.Folder);
            }

            _deleteSaves.Clear();
        }

        public static List<string> _deleteSaves;

        private void LocalizationService_DeletingDictionaryItem(ILocalizationService sender, Umbraco.Core.Events.DeleteEventArgs<IDictionaryItem> e)
        {
            if (uSyncEvents.Paused)
                return;

            foreach (var item in e.DeletedEntities)
            {
                var topItem = GetTop(item.Key);

                if (topItem == null)
                    LogHelper.Info<DictionaryHandler>("Null Top: {0}", () => item.ItemKey);

                if (item.Key != topItem.Key)
                {
                    LogHelper.Info<DictionaryHandler>("Added to Save: {0}", () => topItem.ItemKey);
                    _deleteSaves.Add(topItem.ItemKey);
                    // we just save the topmost item
                }
                else
                {
                    // delete
                    uSyncIOHelper.ArchiveRelativeFile(SyncFolder, item.ItemKey.ToSafeAlias());
                    uSyncBackOfficeContext.Instance.Tracker.AddAction(SyncActionType.Delete, item.ItemKey, typeof(IDictionaryItem));
                }
            }
        }

        private void LocalizationService_SavedDictionaryItem(ILocalizationService sender, Umbraco.Core.Events.SaveEventArgs<IDictionaryItem> e)
        {
            if (uSyncEvents.Paused)
                return;

            foreach (var item in e.SavedEntities)
            {
                LogHelper.Info<DictionaryHandler>("Save: {0}", () => item.ItemKey);
                var topItem = GetTop(item.Key);

                var action = ExportToDisk(topItem, uSyncBackOfficeContext.Instance.Configuration.Settings.Folder);
                if (action.Success)
                {
                    // name checker only really works, when the export has the guid in it.
                    // NameChecker.ManageOrphanFiles(Constants.Packaging.DictionaryItemNodeName, item.Key, action.FileName);
                    uSyncBackOfficeContext.Instance.Tracker.RemoveActions(topItem.ItemKey, typeof(IDictionaryItem));
                }
            }
        }

        private IDictionaryItem GetTop(Guid? id)
        {
            LogHelper.Info<DictionaryHandler>("Get Top: {0}", () => id.Value);

            var item = ApplicationContext.Current.Services.LocalizationService.GetDictionaryItemById(id.Value);
            if (item == null)
            {
                LogHelper.Info<DictionaryHandler>("Failed to Get Item: {0}", () => id.Value);
                return null;
            }

            LogHelper.Info<DictionaryHandler>("Get Top: {0}", () => item.ItemKey);
            if (item.ParentId.HasValue && item.ParentId.Value != Guid.Empty)
                return GetTop(item.ParentId);

            return item;
        }
    }
}