void main()
{
    int i = 0;
    // for (i = 0; i < 3; i++)
    // {
    //     if (i == 1)
    //         continue;
    //     print i;
    // }
    while (i < 3)
    {
        if (i == 1)
        {
            i++;
            continue;
        }
        print i;
        i++;
    }
}