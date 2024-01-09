# Conch Plugin Manager (WIP)

This is a [CounterStrikeSharp](www.google.com) plugin for CS2 that helps you install, update, and remove other plugins that are hosted on Github.

## Supported Plugins

For the most part, if a plugin is packaged as a release (in zip format) on Github, you should be able to use Conch Plugin Manager to install it. 

Notably, plugins that are only available in the repo itself as well as plugins that are packaged as .rar are unsupported, though I plan to add support for these in the future.

## Other Considerations

 - **This is a work in progress**. In the future I might make major changes such as the location of the config file or how the commands work. Also, since I'm still developing this, logging is pretty verbose.
 - CPM queries Github once per package per update, and **you will be rate limited if you reach Github's 60 query/hour limit**. I plan on adding a way to use your own auth token for people who don't want to worry about this,
but until then as long as you have less than 60 packages and you're pretty careful you should be fine.
 - **You cannot use CPM to manage plugins that were installed manually**. If you want CPM to manage these plugins, you'll have to reinstall them using CPM.
 - In the future, CSS may get its own internal plugin manager

## How to Use

### Install
`css_cpm_install <github_author>/<repository_name>`


For example, to install newix's [Soccerball ⚽](https://github.com/newix1/cssharp-soccerball) plugin, use `!cpm_install newix1/cssharp-soccerball`.

Once installed, CPM will attempt to load the plugin as well.

### Update
When you install a plugin using CPM, it will automatically be updated on server restart or when the plugin is reloaded.

In addition, you can use command `css_cpm_update_all` to update all your plugins.

### Remove

`css_cpm_remove <github_author>/<repository_name>` or `css_cpm_remove <plugin_directory>`

For example, to remove the Soccerball plugin, you can do either of the following:

`!cpm_remove newix1/cssharp-soccerball` or `!cpm_remove Soccerball`

### List

`css_cpm_list` 

This gives you a list of plugins managed by CPM including the tag name, download string, and folder name.


## TODO: 

 - [ ] support rar
 - [ ] support release contained in repo
 - [ ] support use of github auth tokens
 - [ ] command to update individual plugin
 - [ ] allow configure auto-update

 
