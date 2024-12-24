﻿using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SentenceStudio.Data;
using SentenceStudio.Models;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Dashboard;

public partial class DashboardPageModel : BaseViewModel
{
    public LocalizationManager Localize => LocalizationManager.Instance;

    [ObservableProperty]
    private List<VocabularyList> _vocabLists;

    [ObservableProperty]
    private VocabularyList _selectedVocabList;

    [ObservableProperty] private List<SkillProfile> _skillProfiles;

    [ObservableProperty] private SkillProfile _selectedSkillProfile;

    public DashboardPageModel(IServiceProvider service)
    {
        _vocabService = service.GetRequiredService<VocabularyService>();
        _userProfileRepository = service.GetRequiredService<UserProfileRepository>();
        _userActivityRepository = service.GetRequiredService<UserActivityRepository>();
        _skillsRepository = service.GetRequiredService<SkillProfileRepository>();
    }

    partial void OnSelectedSkillProfileChanged(SkillProfile oldValue, SkillProfile newValue)
    {
        Preferences.Set(nameof(SelectedSkillProfile), newValue.ID);
    }

    partial void OnSelectedVocabListChanged(VocabularyList oldValue, VocabularyList newValue)
    {
        Preferences.Set(nameof(SelectedVocabList), newValue.PrimaryID);
    }

    public override async Task Refresh()
    {
        await GetLists();
    }   

    public async Task<List<UserActivity>> GetWritingActivity()
    {
        return await _userActivityRepository.ListAsync();
    }

    private async Task GetLists()
    {
        VocabLists = await _vocabService.GetListsAsync();
        if(VocabLists.Count == 0)
        {
            //do we have a profile with languages
            var profile = await _userProfileRepository.GetAsync();
            if(profile != null)
            {
                var lists = await _vocabService.GetListsAsync();
                if(lists.Count == 0)
                {
                    //create default lists
                    var response = await Shell.Current.DisplayAlert("Vocabulary", "Would you like me to create a starter vocabulary list for you?", "Yes", "No, I'll do it myself");
                    if(response){
                        await _vocabService.GetStarterVocabulary(profile.NativeLanguage, profile.TargetLanguage);
                        VocabLists = await _vocabService.GetListsAsync();
                    }
                }
            }else{
                // prompt to create a profile first
                var response = await Shell.Current.DisplayAlert("Profile", "To get started, create a profile and tell us what language you are learning today.", "Let's do it", "Maybe later");
                if(response)
                {
                    await Shell.Current.GoToAsync("///userProfile");
                }
            }
        }else{
            var lastSelected = Preferences.Get(nameof(SelectedVocabList), "0");
            SelectedVocabList = VocabLists.FirstOrDefault(v => v.PrimaryID == int.Parse(lastSelected));
        }

        SkillProfiles = await _skillsRepository.ListAsync();
        if (SkillProfiles.Count > 0)
        {
            var lastSelectedSkillProfile = Preferences.Get(nameof(SelectedSkillProfile), "0");
            SelectedSkillProfile = SkillProfiles.FirstOrDefault(s => s.ID == int.Parse(lastSelectedSkillProfile));
        }
    }   

    public VocabularyService _vocabService { get; }

    private UserProfileRepository _userProfileRepository;
    private UserActivityRepository _userActivityRepository;
    private readonly SkillProfileRepository _skillsRepository;


    [RelayCommand]
    async Task AddVocabulary()
    {
        await Shell.Current.GoToAsync("addVocabulary");
    }

    private bool CanExecuteAddVocabularyCommand()
    {
        return IsConnected;
    }

    [RelayCommand]
    async Task ViewList(int listID)
    {
        await Shell.Current.GoToAsync($"editVocabulary?id={listID}");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
    async Task Play(int listID)
    {
        // if(listID == 0)
        //     listID = VocabLists.First().ID;

        try{
            var payload = new ShellNavigationQueryParameters
                        {
                            {"listID", SelectedVocabList.PrimaryID},
                            {"skillProfileID", SelectedSkillProfile.ID}
                        };
            
            await Shell.Current.GoToAsync($"translation", payload);
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
    async Task SyntacticAnalysis()
    {
        try{
            if(VocabLists.Count == 0)
            VocabLists = await _vocabService.GetListsAsync();

            var listID = VocabLists.First().PrimaryID;
            
            await Shell.Current.GoToAsync($"syntacticAnalysis?listID={listID}");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
        
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCommands))]  
    async Task DescribeAScene()
    {
        // await Shell.Current.DisplayAlert("HR", "Reloaded", "Okay");
        try{
            await Shell.Current.GoToAsync($"describeScene");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCommands))]
    async Task Warmup()
    {
        try{
            await Shell.Current.GoToAsync($"warmup");
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    
    [RelayCommand(CanExecute = nameof(CanExecuteCommands))]  
    async Task Navigate(string route)
    {
        var payload = new ShellNavigationQueryParameters
                        {
                            {"listID", SelectedVocabList.PrimaryID},
                            {"skillProfileID", SelectedSkillProfile.ID}
                        };

        try{
            await Shell.Current.GoToAsync($"{route}", payload);
        }catch(Exception ex)
        {
            Debug.WriteLine($"{ex.Message}");
        }
    }

    // A method that provides recent activity summary to AI
    // and returns a proposed set of activity for the day
    // would need to track progress until either completion or a new day
    
}
