using System;
using SentenceStudio.WebAPI.Models;

namespace SentenceStudio.WebAPI.Services;

public class VocabularyListRepository : IVocabularyListRepository
{
    public IEnumerable<VocabularyList> All => throw new NotImplementedException();

    public void Delete(string id)
    {
        throw new NotImplementedException();
    }

    public bool DoesItemExist(string id)
    {
        throw new NotImplementedException();
    }

    public VocabularyList Find(string id)
    {
        throw new NotImplementedException();
    }

    public void Insert(VocabularyList item)
    {
        throw new NotImplementedException();
    }

    public void Update(VocabularyList item)
    {
        throw new NotImplementedException();
    }
}
