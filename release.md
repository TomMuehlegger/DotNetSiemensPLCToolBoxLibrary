# Release for own NuGet Packages

* Fix everthing in branch `sda_test` and run the tests
* Merge (cherry pick) the commits from `sda_test` to `master`
* Create a pull request to merge `master` to the original repository
* Merge master to release branch
* Go to GitHub --> Code --> Releases --> Draft a new release (for the release branch)
* Add the version number and a description of the release
* Publish the release
* Go to NuGet and update the package with the new version number and description