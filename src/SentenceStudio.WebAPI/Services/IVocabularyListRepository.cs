using System;
using SentenceStudio.WebAPI.Models;

namespace SentenceStudio.WebAPI.Services;

public interface IVocabularyListRepository
{
    bool DoesItemExist(string id);
    IEnumerable<VocabularyList> All { get; }
    VocabularyList Find(string id);
    void Insert(VocabularyList item);
    void Update(VocabularyList item);
    void Delete(string id);

}
