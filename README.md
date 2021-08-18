# CreateRangeBars
C# version of program to read tick data files created by ReadSierraChartSCIDSharp and create cenetered range bars

This program reads tick files from the local SierraChartData directory and creates associated cenetered range bar files
On my machine this is at C:/Users/larry/SierraChartData. I have a private GitHub repo named SierraChartData which also has this data

Each input file name is actually a csv file named: {futures prefix}{futures month code}{2 digit year}.txt

The files only contain at most 1 tick per second starting from the 2200 UTC on the 9th of the first active month to 2200 UTC on the 9th of the expiration month,
a total of 3 months. This is equivalent to 6pm ET on the 9th of the expiry month-3 through 6pm ET on the 9th of the expiry month. For each day, there is data from 6pm ET
through 4:30pm ET of the following day...the current hours (as of 8/5/2021) of the CME futures contracts. For each week, there is data from 6pm ET Sunday through 4:30pm ET Friday.

Each tick is written with an ISO date/time format in the form:

yyyy-mm-ddThh:mm:ss,price

price is a floating point number with 2 digits to the right of the decimal point.

A centered range bar is a range bar that contains data on either side of the opening tick. For instance a 3 tic renage bar with ticks of 0.25
might look like:

The actual ticks:
4100.25, 4100.5, 4100.25, 4100.75, 4100.5, 4100.75, 4101, 4101.25

The range bars would be:
4100.25, 4100.75, 4101.25

So, the range bar whose value is 4100.25 also contains ticks of values 4100 and 4100.5
The range bar whose value is 4100.75 would contain ticks of 4100.5 and 4101
The range bar whose value is 4101.25 would contain ticks of 4101 and 4101.5
So, you can see each of these range bars overlap the previous one (that might not always be the case if the price jumps)

# Programming comments:
This is written using C# 10 and Visual Studio 2022 Preview