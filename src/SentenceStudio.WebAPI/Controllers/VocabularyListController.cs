using System;
using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.AspNetCore.Components;
using SentenceStudio.WebAPI.Data;
using SentenceStudio.WebAPI.Models;
using SentenceStudio.WebAPI.Services;

namespace SentenceStudio.WebAPI.Controllers;

[Route("tables/[controller]")]
public class VocabularyListController : TableController<VocabularyList>
{
    private readonly IVocabularyListRepository _vocabularyListRepository;

        public VocabularyListController(AppDbContext context, IVocabularyListRepository vocabularyListRepository)
            : base(new EntityTableRepository<VocabularyList>(context))
        {
            _vocabularyListRepository = vocabularyListRepository;
            Repository = new EntityTableRepository<VocabularyList>(context);
            Options = new TableControllerOptions 
            {
                DisableClientSideEvaluation = false,
                EnableSoftDelete = false,
                MaxTop = 128000,
                PageSize = 100,
                UnauthorizedStatusCode = 401
            };
        }

}
